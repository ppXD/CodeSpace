using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Closes the decisions a stopped agent run left unanswered. An agent raises a <c>decision.request</c> mid-run and
/// blocks on it as an AwaitingApproval ledger row; a cancel or the reconciler's abandon ends the run, but nothing ended
/// that row, so the question stayed in the team queue and on the Room — answerable into a run that no longer exists,
/// and for a human-required decision forever, since the decision reaper only ever defers those. The stop closes them
/// here instead, as <see cref="ToolCallLedgerStatus.Expired"/>: the no-decision terminal every pending-decision reader
/// already excludes.
///
/// <para>Exactly-once against an answer racing the stop: this is a status-guarded CAS on the same row, from the same
/// AwaitingApproval state, that the answer's own CAS moves to Succeeded — so one of them wins and the other moves
/// nothing. An answer that landed first is kept (this skips its row); one that lands second finds the row Expired and
/// is refused. Filtering the readers by run status instead would decide it in a read before the answer's write, and an
/// answer checked against a live run could still be accepted after the stop.</para>
/// </summary>
public static class StoppedRunDecisions
{
    /// <summary>The reason stamped on a decision its run's stop closed unanswered — and what an answer refused for that reason is told.</summary>
    public const string ExpiredError = "The agent run that asked this was stopped before it was answered, so the question closed unanswered.";

    /// <summary>
    /// Expire every still-unanswered <c>decision.request</c> row <paramref name="agentRunId"/> raised. Called once the
    /// run's own terminal has committed, so it never throws — not even on shutdown, which must not skip the cleanup that
    /// follows it; a row it could not close stays open until its deadline handling, as before.
    /// </summary>
    public static async Task ExpireQuietlyAsync(CodeSpaceDbContext db, Guid agentRunId, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await db.ToolCallLedger
                .Where(l => l.AgentRunId == agentRunId && l.ToolKind == DecisionToolKinds.DecisionRequest && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.Status, ToolCallLedgerStatus.Expired)
                    .SetProperty(l => l.Error, ExpiredError)
                    .SetProperty(l => l.LastModifiedDate, DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Agent run {AgentRunId} stopped, but its unanswered decisions could not be closed; they stay open until their deadline handling", agentRunId);
        }
    }
}
