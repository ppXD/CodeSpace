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
/// already excludes. A run that ends on its own — its completion, or its recovery from the spool — closes them the same
/// way, saying so with <see cref="EndedError"/>.
///
/// <para>Exactly-once against an answer racing the stop: this is a status-guarded CAS on the same row, from the same
/// AwaitingApproval state, that the answer's own CAS moves to Succeeded — so one of them wins and the other moves
/// nothing. An answer that landed first is kept (this skips its row); one that lands second finds the row Expired and
/// is refused. Filtering the readers by run status instead would decide it in a read before the answer's write, and an
/// answer checked against a live run could still be accepted after the stop.</para>
///
/// <para>Every writer here that locks more than one of a run's decisions takes them in id order — the close below, and
/// the lock a terminal takes before it reads them — so a stop closing a run's decisions and that run's own end wait for
/// each other instead of deadlocking. A deadlock was worse than a wait on both sides: a close that lost it was
/// swallowed, leaving the questions open on a finished run, and an end that lost it became an executor-error.</para>
/// </summary>
public static class StoppedRunDecisions
{
    /// <summary>The reason stamped on a decision its run's stop closed unanswered — and what an answer refused for that reason is told.</summary>
    public const string ExpiredError = "The agent run that asked this was stopped before it was answered, so the question closed unanswered.";

    /// <summary>The reason stamped on a decision its run's own end closed unanswered — the run succeeded, failed or was recovered with the question still open — and what an answer refused for that reason is told.</summary>
    public const string EndedError = "The agent run that asked this ended before it was answered, so the question closed unanswered.";

    /// <summary>
    /// Expire every still-unanswered <c>decision.request</c> row <paramref name="agentRunId"/> raised. Called once the
    /// run's own terminal has committed, so it never throws — not even on shutdown, which must not skip the cleanup that
    /// follows it. A row it could not close stays open: one with a default still gets it at its deadline, but a
    /// human-required one is only ever deferred, so it waits for a person indefinitely.
    /// </summary>
    public static async Task ExpireQuietlyAsync(CodeSpaceDbContext db, Guid agentRunId, ILogger logger, CancellationToken cancellationToken) =>
        await ExpireQuietlyAsync(db, agentRunId, ExpiredError, logger, cancellationToken).ConfigureAwait(false);

    /// <summary>As <see cref="ExpireQuietlyAsync(CodeSpaceDbContext, Guid, ILogger, CancellationToken)"/>, stamping <paramref name="reason"/>.</summary>
    public static async Task ExpireQuietlyAsync(CodeSpaceDbContext db, Guid agentRunId, string reason, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await ExpireAsync(db, agentRunId, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Agent run {AgentRunId} ended, but its unanswered decisions could not be closed; one with a default still gets it at its deadline, but a human-required one stays open until someone answers it", agentRunId);
        }
    }

    /// <summary>How long a terminal transaction waits on any one lock before failing with 55P03 — well under Npgsql's 30-second command timeout, so a lock a hung session strands surfaces as the "not now" the terminal writer offers once more, not as a command timeout.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private static readonly string LockTimeoutSetting = $"{(int)LockTimeout.TotalMilliseconds}ms";

    /// <summary>
    /// Bound every lock wait of the caller's transaction by <see cref="LockTimeout"/>, for that transaction alone. A
    /// terminal transaction waits on the run's decisions and on the run's row; a session holding one of them and hung used
    /// to hold the terminal for the whole command timeout, and then again for its one retry.
    /// </summary>
    public static async Task BoundLockWaitsAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('lock_timeout', {LockTimeoutSetting}, true)", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Lock every <c>decision.request</c> row <paramref name="agentRunId"/> raised until the caller's transaction ends —
    /// taken by a terminal write BEFORE it reads which of them are still unanswered, so an answer's CAS on any of them
    /// either committed before that read (and the read sees it answered) or waits for the terminal to commit and finds
    /// the row it closed. Ordered, so two writers racing one run's terminal lock them in the same order.
    /// </summary>
    public static async Task LockAsync(CodeSpaceDbContext db, Guid agentRunId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM tool_call_ledger WHERE agent_run_id = {agentRunId} AND tool_kind = {DecisionToolKinds.DecisionRequest} ORDER BY id FOR UPDATE").ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The CAS itself: every still-unanswered <c>decision.request</c> row <paramref name="agentRunId"/> raised → Expired,
    /// stamped with <paramref name="reason"/>. It locks the rows in id order before it writes them, in the one statement —
    /// the order <see cref="LockAsync"/> takes them in; a plain UPDATE locks them in whatever order its scan meets them.
    /// </summary>
    public static async Task ExpireAsync(CodeSpaceDbContext db, Guid agentRunId, string reason, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"WITH locked AS MATERIALIZED (SELECT id FROM tool_call_ledger WHERE agent_run_id = {agentRunId} AND tool_kind = {DecisionToolKinds.DecisionRequest} AND status = {nameof(ToolCallLedgerStatus.AwaitingApproval)} AND approved_at IS NULL ORDER BY id FOR UPDATE) UPDATE tool_call_ledger AS target SET status = {nameof(ToolCallLedgerStatus.Expired)}, error = {reason}, last_modified_date = {DateTimeOffset.UtcNow} FROM locked WHERE target.id = locked.id AND target.status = {nameof(ToolCallLedgerStatus.AwaitingApproval)} AND target.approved_at IS NULL", cancellationToken).ConfigureAwait(false);
}
