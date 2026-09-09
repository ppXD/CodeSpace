using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Learning;

public sealed record PlannerLessonSelection(string Arm, IReadOnlyList<Lesson> Lessons, IReadOnlyList<Guid> LessonIds);

public interface IPlannerLessonMemory
{
    Task<PlannerLessonSelection> ResolveAsync(WorkflowPlanRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves one bounded semantic lesson set per distinct planner prompt and freezes workflow-backed results before the planner call.</summary>
public sealed class PlannerLessonMemory : IPlannerLessonMemory, IScopedDependency
{
    private const string WithheldStatus = "withheld";

    private readonly CodeSpaceDbContext _db;
    private readonly ILessonReader _lessons;
    private readonly ILogger<PlannerLessonMemory> _logger;

    public PlannerLessonMemory(CodeSpaceDbContext db, ILessonReader lessons, ILogger<PlannerLessonMemory> logger)
    {
        _db = db;
        _lessons = lessons;
        _logger = logger;
    }

    public async Task<PlannerLessonSelection> ResolveAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
    {
        var taskNeed = TaskNeed(request);
        var promptKey = PromptKey(request, taskNeed);
        if (request.WorkflowRunId is { } runId && await ReadAsync(runId, request.TeamId, promptKey, cancellationToken).ConfigureAwait(false) is { } frozen)
            return await MaterializeAsync(frozen, cancellationToken).ConfigureAwait(false);

        var runtime = LessonRuntimeContext.General(request.RepositoryId);
        var current = (await _lessons.ListCurrentAsync(new LessonReadRequest(request.TeamId, RunModeKeys.PlanMap, runtime, DateTimeOffset.UtcNow, LessonReader.MaxTake), cancellationToken).ConfigureAwait(false)).Take(LlmLessonRelevanceEvaluator.MaxCandidates).ToList();
        var arm = LessonArms.For(request.TeamId, request.TaskGoal ?? request.TaskText, current.Count);
        var relevance = arm == LessonArms.Injected
            ? await SelectInsideLearningScopeAsync(new LessonRelevanceRequest(request.TeamId, taskNeed, current, LessonArms.TopK), cancellationToken).ConfigureAwait(false)
            : new LessonRelevanceResult([], current.Select(lesson => lesson.Id).ToList(), arm == LessonArms.None ? LessonRelevanceStatuses.NoCandidates : WithheldStatus, null, null);

        if (request.WorkflowRunId is not { } workflowRunId)
            return new PlannerLessonSelection(arm, relevance.Lessons, relevance.Lessons.Select(lesson => lesson.Id).ToList());

        var proposal = new ReceiptRow(workflowRunId, request.TeamId, promptKey, arm, relevance.Lessons.Select(lesson => lesson.Id).ToArray(), relevance.CandidateIds.ToArray(), relevance.Status, relevance.ObservedModel, LlmLessonRelevanceEvaluator.Generation, relevance.AssessmentDigest);
        var receipt = await GetOrCreateAsync(proposal, cancellationToken).ConfigureAwait(false);
        return await MaterializeAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LessonRelevanceResult> SelectInsideLearningScopeAsync(LessonRelevanceRequest request, CancellationToken cancellationToken)
    {
        var current = LlmCallContext.Current;
        using var scope = current is null ? null : LlmCallContext.Push(current with { Kind = LlmLessonRelevanceEvaluator.CallKind });
        return await _lessons.SelectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlannerLessonSelection> MaterializeAsync(ReceiptRow receipt, CancellationToken cancellationToken)
    {
        var rows = receipt.LessonIds.Length == 0
            ? []
            : await _db.Lesson.AsNoTracking().Where(lesson => lesson.TeamId == receipt.TeamId && receipt.LessonIds.Contains(lesson.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = rows.ToDictionary(lesson => lesson.Id);
        var historical = receipt.LessonIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (historical.Count != receipt.LessonIds.Length) _logger.LogWarning("A frozen planner lesson receipt references unavailable history. WorkflowRunId={WorkflowRunId} PromptKey={PromptKey} Expected={Expected} Found={Found}", receipt.WorkflowRunId, receipt.PromptKey, receipt.LessonIds.Length, historical.Count);
        return new PlannerLessonSelection(receipt.LessonArm, historical, receipt.LessonIds);
    }

    private async Task<ReceiptRow?> ReadAsync(Guid workflowRunId, Guid teamId, string promptKey, CancellationToken cancellationToken) =>
        await _db.Database.SqlQuery<ReceiptRow>($"""
            SELECT workflow_run_id, team_id, prompt_key, lesson_arm, lesson_ids, candidate_ids, relevance_status, relevance_model, relevance_generation, assessment_digest
            FROM planner_lesson_prompt_receipt
            WHERE workflow_run_id = {workflowRunId} AND team_id = {teamId} AND prompt_key = {promptKey}
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<ReceiptRow> GetOrCreateAsync(ReceiptRow proposal, CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO planner_lesson_prompt_receipt (workflow_run_id, team_id, prompt_key, lesson_arm, lesson_ids, candidate_ids, relevance_status, relevance_model, relevance_generation, assessment_digest, selected_at)
            SELECT {proposal.WorkflowRunId}, {proposal.TeamId}, {proposal.PromptKey}, {proposal.LessonArm}, {proposal.LessonIds}, {proposal.CandidateIds}, {proposal.RelevanceStatus}, {proposal.RelevanceModel}, {proposal.RelevanceGeneration}, {proposal.AssessmentDigest}, clock_timestamp()
            FROM workflow_run WHERE id = {proposal.WorkflowRunId} AND team_id = {proposal.TeamId}
            ON CONFLICT (workflow_run_id, prompt_key) DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(proposal.WorkflowRunId, proposal.TeamId, proposal.PromptKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The workflow run is unavailable in the requested team scope.");
    }

    internal static string TaskNeed(WorkflowPlanRequest request) => string.IsNullOrWhiteSpace(request.ReviewerCritique) ? request.TaskText : $"{request.TaskText}\n\nPlanner revision need:\n{request.ReviewerCritique}";

    internal static string PromptKey(WorkflowPlanRequest request, string taskNeed)
    {
        var identity = $"planner-v1\n{request.NodeId?.Trim()}\n{request.RepositoryId:D}\n{taskNeed}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private sealed record ReceiptRow(Guid WorkflowRunId, Guid TeamId, string PromptKey, string LessonArm, Guid[] LessonIds, Guid[] CandidateIds, string RelevanceStatus, string? RelevanceModel, string RelevanceGeneration, string? AssessmentDigest);
}
