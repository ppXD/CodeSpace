using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Learning;

public sealed record AgentLessonPromptReceipt(Guid WorkflowRunId, Guid TeamId, string PromptKey, IReadOnlyList<Guid> LessonIds)
{
    public IReadOnlyList<Guid> CandidateIds { get; init; } = [];
    public string RelevanceStatus { get; init; } = LessonRelevanceStatuses.Legacy;
    public string? RelevanceModel { get; init; }
    public string RelevanceGeneration { get; init; } = LessonRelevanceStatuses.Legacy;
    public string? AssessmentDigest { get; init; }
}

public sealed record AgentLessonPromptReceiptProposal(Guid WorkflowRunId, Guid TeamId, string PromptKey, IReadOnlyList<Guid> LessonIds)
{
    public IReadOnlyList<Guid> CandidateIds { get; init; } = [];
    public string RelevanceStatus { get; init; } = LessonRelevanceStatuses.Legacy;
    public string? RelevanceModel { get; init; }
    public string RelevanceGeneration { get; init; } = LessonRelevanceStatuses.Legacy;
    public string? AssessmentDigest { get; init; }
}

public interface IAgentLessonPromptReceiptStore
{
    Task<AgentLessonPromptReceipt?> ReadAsync(Guid workflowRunId, Guid teamId, string promptKey, CancellationToken cancellationToken);
    Task<AgentLessonPromptReceipt> GetOrCreateAsync(AgentLessonPromptReceiptProposal proposal, CancellationToken cancellationToken);
}

public sealed class AgentLessonPromptReceiptStore : IAgentLessonPromptReceiptStore, IScopedDependency
{
    public const int MaxLessons = 10;

    private readonly CodeSpaceDbContext _db;

    public AgentLessonPromptReceiptStore(CodeSpaceDbContext db) => _db = db;

    public async Task<AgentLessonPromptReceipt?> ReadAsync(Guid workflowRunId, Guid teamId, string promptKey, CancellationToken cancellationToken)
    {
        if (!IsPromptKey(promptKey)) throw new ArgumentOutOfRangeException(nameof(promptKey), promptKey, "The prompt key must be a lowercase SHA-256 digest.");
        var row = await _db.Database.SqlQuery<ReceiptRow>($"""
            SELECT workflow_run_id, team_id, prompt_key, lesson_ids, candidate_ids, relevance_status, relevance_model, relevance_generation, assessment_digest
            FROM agent_lesson_prompt_receipt
            WHERE workflow_run_id = {workflowRunId} AND team_id = {teamId} AND prompt_key = {promptKey}
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : Materialize(row);
    }

    public async Task<AgentLessonPromptReceipt> GetOrCreateAsync(AgentLessonPromptReceiptProposal proposal, CancellationToken cancellationToken)
    {
        if (!IsPromptKey(proposal.PromptKey)) throw new ArgumentOutOfRangeException(nameof(proposal), proposal.PromptKey, "The prompt key must be a lowercase SHA-256 digest.");
        var lessonIds = proposal.LessonIds.Distinct().Take(MaxLessons).ToArray();
        var candidateIds = proposal.CandidateIds.Distinct().Take(LlmLessonRelevanceEvaluator.MaxCandidates).ToArray();
        if (!IsStatus(proposal.RelevanceStatus)) throw new ArgumentOutOfRangeException(nameof(proposal), proposal.RelevanceStatus, "Unknown relevance status.");
        if (proposal.RelevanceStatus != LessonRelevanceStatuses.Legacy && lessonIds.Except(candidateIds).Any()) throw new ArgumentException("Selected lessons must be a subset of the assessed candidates.", nameof(proposal));
        if (proposal.RelevanceStatus != LessonRelevanceStatuses.Legacy && (proposal.RelevanceStatus == LessonRelevanceStatuses.Selected) != (lessonIds.Length > 0)) throw new ArgumentException("The relevance status and selected lesson ids disagree.", nameof(proposal));
        if (proposal.RelevanceStatus != LessonRelevanceStatuses.Legacy && (proposal.RelevanceStatus == LessonRelevanceStatuses.NoCandidates) != (candidateIds.Length == 0)) throw new ArgumentException("The relevance status and candidate ids disagree.", nameof(proposal));
        if ((proposal.RelevanceStatus is LessonRelevanceStatuses.Selected or LessonRelevanceStatuses.Abstained) && proposal.AssessmentDigest is null) throw new ArgumentException("A completed relevance assessment requires its response digest.", nameof(proposal));
        if (string.IsNullOrWhiteSpace(proposal.RelevanceGeneration) || proposal.RelevanceGeneration.Length > 100) throw new ArgumentOutOfRangeException(nameof(proposal), proposal.RelevanceGeneration, "The relevance generation must contain at most 100 characters.");
        if (proposal.RelevanceModel?.Length > 500) throw new ArgumentOutOfRangeException(nameof(proposal), proposal.RelevanceModel, "The observed relevance model must contain at most 500 characters.");
        if (proposal.AssessmentDigest is not null && !IsPromptKey(proposal.AssessmentDigest)) throw new ArgumentOutOfRangeException(nameof(proposal), proposal.AssessmentDigest, "The assessment digest must be a lowercase SHA-256 digest.");

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO agent_lesson_prompt_receipt (workflow_run_id, team_id, prompt_key, lesson_ids, selected_at, candidate_ids, relevance_status, relevance_model, relevance_generation, assessment_digest)
            SELECT {proposal.WorkflowRunId}, {proposal.TeamId}, {proposal.PromptKey}, {lessonIds}, clock_timestamp(), {candidateIds}, {proposal.RelevanceStatus}, {proposal.RelevanceModel}, {proposal.RelevanceGeneration}, {proposal.AssessmentDigest}
            FROM workflow_run
            WHERE id = {proposal.WorkflowRunId} AND team_id = {proposal.TeamId}
            ON CONFLICT (workflow_run_id, prompt_key) DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(proposal.WorkflowRunId, proposal.TeamId, proposal.PromptKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The workflow run is unavailable in the requested team scope.");
    }

    private static bool IsPromptKey(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsStatus(string value) => value is LessonRelevanceStatuses.Selected or LessonRelevanceStatuses.Abstained or LessonRelevanceStatuses.Unavailable or LessonRelevanceStatuses.Failed or LessonRelevanceStatuses.NoCandidates or LessonRelevanceStatuses.Legacy;

    private static AgentLessonPromptReceipt Materialize(ReceiptRow row) => new(row.WorkflowRunId, row.TeamId, row.PromptKey, row.LessonIds)
    {
        CandidateIds = row.CandidateIds,
        RelevanceStatus = row.RelevanceStatus,
        RelevanceModel = row.RelevanceModel,
        RelevanceGeneration = row.RelevanceGeneration,
        AssessmentDigest = row.AssessmentDigest,
    };

    private sealed record ReceiptRow(Guid WorkflowRunId, Guid TeamId, string PromptKey, Guid[] LessonIds, Guid[] CandidateIds, string RelevanceStatus, string? RelevanceModel, string RelevanceGeneration, string? AssessmentDigest);
}
