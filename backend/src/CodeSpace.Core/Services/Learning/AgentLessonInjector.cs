using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeSpace.Core.Services.Learning;

public interface IAgentLessonInjector
{
    Task<AgentTask> InjectAsync(AgentLessonInjectionRequest request, CancellationToken cancellationToken);
}

public sealed record AgentLessonInjectionRequest(AgentTask Task, Guid TeamId, Guid? WorkflowRunId, string? NodeId, string IterationKey);

/// <summary>
/// The one agent-runtime lesson boundary. Every workflow-backed agent, independent of projection, receives the run's
/// frozen arm and a per-logical-agent, per-runtime exact receipt here after authority admission and before its immutable task envelope is persisted.
/// </summary>
public sealed class AgentLessonInjector : IAgentLessonInjector, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILessonReader _lessons;
    private readonly IRunLessonAssignmentStore _assignments;
    private readonly IAgentLessonPromptReceiptStore _promptReceipts;
    private readonly ILogger<AgentLessonInjector> _logger;

    public AgentLessonInjector(CodeSpaceDbContext db, ILessonReader lessons, IRunLessonAssignmentStore assignments, IAgentLessonPromptReceiptStore promptReceipts, ILogger<AgentLessonInjector> logger)
    {
        _db = db;
        _lessons = lessons;
        _assignments = assignments;
        _promptReceipts = promptReceipts;
        _logger = logger;
    }

    public async Task<AgentTask> InjectAsync(AgentLessonInjectionRequest request, CancellationToken cancellationToken)
    {
        if (request.WorkflowRunId is not { } runId) return request.Task;

        var assignment = await _assignments.ReadAsync(runId, request.TeamId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            var proposal = await ProposeAssignmentAsync(request, runId, cancellationToken).ConfigureAwait(false);
            assignment = await _assignments.GetOrCreateAsync(proposal, cancellationToken).ConfigureAwait(false);
        }

        if (assignment.Arm != LessonArms.Injected) return AgentLessonPrompt.Inject(request.Task, assignment.Arm, []) with { LessonIds = [] };

        var promptKey = PromptKey(request);
        var receipt = await _promptReceipts.ReadAsync(runId, request.TeamId, promptKey, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            var proposedLessonIds = await ProposePromptLessonsAsync(request, assignment, promptKey, cancellationToken).ConfigureAwait(false);
            receipt = await _promptReceipts.GetOrCreateAsync(new(runId, request.TeamId, promptKey, proposedLessonIds), cancellationToken).ConfigureAwait(false);
        }

        var historical = await ReadHistoricalAsync(receipt.LessonIds, request.TeamId, cancellationToken).ConfigureAwait(false);
        var result = AgentLessonPrompt.Inject(request.Task, assignment.Arm, historical) with { LessonIds = receipt.LessonIds.ToList() };
        if (historical.Count != receipt.LessonIds.Count) _logger.LogWarning("A frozen agent lesson receipt references unavailable history. WorkflowRunId={WorkflowRunId} PromptKey={PromptKey} Expected={Expected} Found={Found}", runId, promptKey, receipt.LessonIds.Count, historical.Count);
        return result;
    }

    private async Task<RunLessonAssignmentProposal> ProposeAssignmentAsync(AgentLessonInjectionRequest request, Guid runId, CancellationToken cancellationToken)
    {
        var frozen = await RunLessonReceiptReader.ReadAsync(_db, runId, request.TeamId, cancellationToken).ConfigureAwait(false);
        if (frozen is not null)
        {
            return new(runId, request.TeamId, frozen.Arm, frozen.LessonIds);
        }

        var mode = await RunModeReader.DeriveAsync(_db, runId, request.TeamId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var hasAnyLesson = await LessonReader.HasCurrentAsync(_db, new LessonAvailabilityRequest(request.TeamId, mode, request.Task.RepositoryId, now), cancellationToken).ConfigureAwait(false);
        var operatorGoal = await ReadOperatorGoalAsync(runId, request.TeamId, cancellationToken).ConfigureAwait(false) ?? request.Task.DisplayTitle ?? request.Task.Goal;
        return new(runId, request.TeamId, LessonArms.For(request.TeamId, operatorGoal, hasAnyLesson ? 1 : 0), []);
    }

    private async Task<IReadOnlyList<Guid>> ProposePromptLessonsAsync(AgentLessonInjectionRequest request, RunLessonAssignment assignment, string promptKey, CancellationToken cancellationToken)
    {
        if (await ReadLegacyPromptReceiptAsync(request, promptKey, cancellationToken).ConfigureAwait(false) is { } legacy) return legacy;

        var mode = await RunModeReader.DeriveAsync(_db, assignment.WorkflowRunId, request.TeamId, cancellationToken).ConfigureAwait(false);
        var runtime = new LessonRuntimeContext(request.Task.RepositoryId, request.Task.Model, request.Task.Harness, request.Task.Tools);
        var current = await _lessons.ListCurrentAsync(new LessonReadRequest(request.TeamId, mode, runtime, DateTimeOffset.UtcNow, LessonArms.TopK), cancellationToken).ConfigureAwait(false);
        var inherited = await ReadHistoricalAsync(assignment.LessonIds, request.TeamId, cancellationToken).ConfigureAwait(false);
        var compatibleInherited = inherited.Where(lesson => LessonApplicability.AppliesTo(runtime, lesson.ApplicableModels, lesson.ApplicableHarnesses, lesson.RequiredTools)).Select(lesson => lesson.Id);
        return current.Select(lesson => lesson.Id).Concat(compatibleInherited).Distinct().Take(AgentLessonPromptReceiptStore.MaxLessons).ToList();
    }

    private async Task<IReadOnlyList<Guid>?> ReadLegacyPromptReceiptAsync(AgentLessonInjectionRequest request, string promptKey, CancellationToken cancellationToken)
    {
        if (request.WorkflowRunId is not { } runId) return null;
        var rows = await LegacyRowsAsync(request, runId, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var prior = DeserializeTask(row.TaskJson);
            if (prior?.LessonArm != LessonArms.Injected || prior.LessonIds is null) continue;
            var priorRequest = new AgentLessonInjectionRequest(prior, request.TeamId, runId, row.NodeId, row.IterationKey ?? "");
            if (PromptKey(priorRequest) == promptKey) return prior.LessonIds.Distinct().Take(AgentLessonPromptReceiptStore.MaxLessons).ToList();
        }

        return null;
    }

    private Task<List<LegacyAgentRow>> LegacyRowsAsync(AgentLessonInjectionRequest request, Guid runId, CancellationToken cancellationToken)
    {
        if (request.Task.WorkUnit is { } workUnit)
            return _db.Database.SqlQuery<LegacyAgentRow>($"""
                SELECT node_id, iteration_key, task_jsonb::text AS task_json
                FROM agent_run
                WHERE workflow_run_id = {runId} AND team_id = {request.TeamId}
                  AND task_jsonb -> 'workUnit' ->> 'workPlanId' = {workUnit.WorkPlanId.ToString()}
                  AND task_jsonb -> 'workUnit' ->> 'planVersion' = {workUnit.PlanVersion.ToString()}
                  AND task_jsonb -> 'workUnit' ->> 'unitId' = {workUnit.UnitId}
                ORDER BY created_date DESC, id DESC LIMIT 20
                """).ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Task.SubtaskId))
            return _db.Database.SqlQuery<LegacyAgentRow>($"""
                SELECT node_id, iteration_key, task_jsonb::text AS task_json
                FROM agent_run
                WHERE workflow_run_id = {runId} AND team_id = {request.TeamId}
                  AND task_jsonb ->> 'subtaskId' = {request.Task.SubtaskId}
                ORDER BY created_date DESC, id DESC LIMIT 20
                """).ToListAsync(cancellationToken);

        return _db.Database.SqlQuery<LegacyAgentRow>($"""
            SELECT node_id, iteration_key, task_jsonb::text AS task_json
            FROM agent_run
            WHERE workflow_run_id = {runId} AND team_id = {request.TeamId}
              AND node_id IS NOT DISTINCT FROM {request.NodeId} AND iteration_key = {request.IterationKey}
            ORDER BY created_date DESC, id DESC LIMIT 20
            """).ToListAsync(cancellationToken);
    }

    internal static string PromptKey(AgentLessonInjectionRequest request)
    {
        var task = request.Task;
        var logical = task.WorkUnit is { } workUnit
            ? $"work-unit\n{workUnit.WorkPlanId:D}\n{workUnit.PlanVersion}\n{workUnit.UnitId}\n{workUnit.ContractHash}\n{workUnit.RequirementRevision}"
            : !string.IsNullOrWhiteSpace(task.SubtaskId) ? $"subtask\n{task.SubtaskId.Trim()}" : $"cell\n{request.NodeId?.Trim()}\n{request.IterationKey}";
        var tools = string.Join('\n', LessonApplicability.Normalize(task.Tools));
        var runtime = $"{task.RepositoryId:D}\n{LessonApplicability.Normalize(task.Model)}\n{LessonApplicability.Normalize(task.Harness)}\n{tools}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logical + "\n---runtime---\n" + runtime))).ToLowerInvariant();
    }

    private static AgentTask? DeserializeTask(string json)
    {
        try { return JsonSerializer.Deserialize<AgentTask>(json, Agents.AgentJson.Options); }
        catch (JsonException) { return null; }
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

    private sealed record LegacyAgentRow(string? NodeId, string? IterationKey, string TaskJson);
}
