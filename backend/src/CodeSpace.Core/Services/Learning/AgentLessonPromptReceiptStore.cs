using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Learning;

public sealed record AgentLessonPromptReceipt(Guid WorkflowRunId, Guid TeamId, string PromptKey, IReadOnlyList<Guid> LessonIds);

public sealed record AgentLessonPromptReceiptProposal(Guid WorkflowRunId, Guid TeamId, string PromptKey, IReadOnlyList<Guid> LessonIds);

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
            SELECT workflow_run_id, team_id, prompt_key, lesson_ids
            FROM agent_lesson_prompt_receipt
            WHERE workflow_run_id = {workflowRunId} AND team_id = {teamId} AND prompt_key = {promptKey}
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : Materialize(row);
    }

    public async Task<AgentLessonPromptReceipt> GetOrCreateAsync(AgentLessonPromptReceiptProposal proposal, CancellationToken cancellationToken)
    {
        if (!IsPromptKey(proposal.PromptKey)) throw new ArgumentOutOfRangeException(nameof(proposal), proposal.PromptKey, "The prompt key must be a lowercase SHA-256 digest.");
        var lessonIds = proposal.LessonIds.Distinct().Take(MaxLessons).ToArray();

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO agent_lesson_prompt_receipt (workflow_run_id, team_id, prompt_key, lesson_ids, selected_at)
            SELECT {proposal.WorkflowRunId}, {proposal.TeamId}, {proposal.PromptKey}, {lessonIds}, clock_timestamp()
            FROM workflow_run
            WHERE id = {proposal.WorkflowRunId} AND team_id = {proposal.TeamId}
            ON CONFLICT (workflow_run_id, prompt_key) DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(proposal.WorkflowRunId, proposal.TeamId, proposal.PromptKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The workflow run is unavailable in the requested team scope.");
    }

    private static bool IsPromptKey(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AgentLessonPromptReceipt Materialize(ReceiptRow row) => new(row.WorkflowRunId, row.TeamId, row.PromptKey, row.LessonIds);

    private sealed record ReceiptRow(Guid WorkflowRunId, Guid TeamId, string PromptKey, Guid[] LessonIds);
}
