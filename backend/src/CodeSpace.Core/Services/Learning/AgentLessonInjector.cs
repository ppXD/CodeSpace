using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Learning;

public interface IAgentLessonInjector
{
    Task<AgentTask> InjectAsync(AgentTask task, Guid teamId, Guid? workflowRunId, CancellationToken cancellationToken);
}

/// <summary>
/// The one agent-runtime lesson boundary. Every workflow-backed agent, independent of projection, receives the run's
/// frozen arm and exact receipts here after authority admission and before its immutable task envelope is persisted.
/// </summary>
public sealed class AgentLessonInjector : IAgentLessonInjector, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILessonReader _lessons;
    private readonly IRunLessonAssignmentStore _assignments;
    private readonly ILogger<AgentLessonInjector> _logger;

    public AgentLessonInjector(CodeSpaceDbContext db, ILessonReader lessons, IRunLessonAssignmentStore assignments, ILogger<AgentLessonInjector> logger)
    {
        _db = db;
        _lessons = lessons;
        _assignments = assignments;
        _logger = logger;
    }

    public async Task<AgentTask> InjectAsync(AgentTask task, Guid teamId, Guid? workflowRunId, CancellationToken cancellationToken)
    {
        if (workflowRunId is not { } runId) return task;

        var assignment = await _assignments.ReadAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            var proposal = await ProposeAsync(task, teamId, runId, cancellationToken).ConfigureAwait(false);
            assignment = await _assignments.GetOrCreateAsync(proposal, cancellationToken).ConfigureAwait(false);
        }

        var historical = assignment.Arm == LessonArms.Injected ? await ReadHistoricalAsync(assignment.LessonIds, teamId, cancellationToken).ConfigureAwait(false) : [];
        var result = AgentLessonPrompt.Inject(task, assignment.Arm, historical) with { LessonIds = assignment.LessonIds.ToList() };
        if (historical.Count != assignment.LessonIds.Count) _logger.LogWarning("A frozen agent lesson receipt references unavailable history. WorkflowRunId={WorkflowRunId} Expected={Expected} Found={Found}", runId, assignment.LessonIds.Count, historical.Count);
        return result;
    }

    private async Task<RunLessonAssignmentProposal> ProposeAsync(AgentTask task, Guid teamId, Guid runId, CancellationToken cancellationToken)
    {
        var frozen = await RunLessonReceiptReader.ReadAsync(_db, runId, teamId, cancellationToken).ConfigureAwait(false);
        if (frozen is not null)
        {
            return new(runId, teamId, frozen.Arm, frozen.LessonIds);
        }

        var mode = await RunModeReader.DeriveAsync(_db, runId, teamId, cancellationToken).ConfigureAwait(false);
        var runtime = new LessonRuntimeContext(task.RepositoryId, task.Model, task.Harness, task.Tools);
        var current = await _lessons.ListCurrentAsync(new LessonReadRequest(teamId, mode, runtime, DateTimeOffset.UtcNow, LessonArms.TopK), cancellationToken).ConfigureAwait(false);
        var operatorGoal = await ReadOperatorGoalAsync(runId, teamId, cancellationToken).ConfigureAwait(false) ?? task.DisplayTitle ?? task.Goal;
        var arm = LessonArms.For(teamId, operatorGoal, current.Count);
        return new(runId, teamId, arm, arm == LessonArms.Injected ? current.Select(lesson => lesson.Id).ToList() : []);
    }

    private async Task<string?> ReadOperatorGoalAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var payload = await _db.WorkflowRun.AsNoTracking().Where(run => run.Id == runId && run.TeamId == teamId)
            .Join(_db.WorkflowRunRequest.AsNoTracking().Where(request => request.TeamId == teamId), run => run.RunRequestId, request => request.Id, (run, request) => request.NormalizedPayloadJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("goal", out var goal)
                && goal.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(goal.GetString()) ? goal.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<Lesson>> ReadHistoricalAsync(IReadOnlyList<Guid> ids, Guid teamId, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];
        var rows = await _db.Lesson.AsNoTracking().Where(lesson => lesson.TeamId == teamId && ids.Contains(lesson.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = rows.ToDictionary(lesson => lesson.Id);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }
}
