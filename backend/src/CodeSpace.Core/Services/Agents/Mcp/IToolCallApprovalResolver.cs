using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Records the human's DECISION (approve / reject) on a parked tool-call approval (durable mid-turn HITL, item D) and
/// wakes any in-memory waiter so a blocked handler call (item D2) resumes. It does NOT run the side effect — approve
/// only STAMPS the decision (the row stays <c>AwaitingApproval</c>; the handler flips it to terminal once it executes);
/// reject drives an undecided <c>AwaitingApproval → Failed</c> directly, and with it every other undecided call of the run
/// on the same target. Both CASes require a not-yet-approved row, so the row takes the first decision even from two cards
/// carrying one token. Owns the status-guarded CAS over the
/// ledger row (mirrors <see cref="ToolCallLedgerService"/>.RecordTerminalAsync) and team-scopes every read for defense-in-depth (mirrors
/// <c>WorkflowResumeService.ResumeByActionTokenAsync</c>). Returns an <see cref="ActionResumeResult"/> so the chat
/// caller knows whether to stamp the card (Resumed / NoWait) or reject a late click (AlreadyResolved).
/// </summary>
public interface IToolCallApprovalResolver
{
    /// <summary>Record an approve/reject verdict on the approval whose token is <paramref name="token"/>, team-scoped to <paramref name="teamId"/>. Resumed when this click decided it; NoWait when no parked approval exists for the team (or the responseKey isn't approve/reject); AlreadyResolved when a deadline / another responder / the handler already moved the row.</summary>
    Task<ActionResumeResult> ResolveByTokenAsync(string token, string responseKey, Guid actorUserId, Guid teamId, CancellationToken ct);
}

public sealed class ToolCallApprovalResolver : IToolCallApprovalResolver, IScopedDependency
{
    /// <summary>
    /// The error a rejected row carries — and, because the row is terminal, the exact text the blocked call and every
    /// identical re-call replay to the model. Load-bearing: the session-effect receipts recognise it to say nothing ran
    /// instead of "outcome uncertain". It names no one: the reviewer is on the row's <c>last_modified_by</c> and on the
    /// card's resolution, and a raw user id tells the model nothing.
    /// </summary>
    public const string RejectedError = "A reviewer rejected this tool call before it ran, so nothing was executed. Re-issuing it with identical arguments returns this same rejection without asking again; change the approach rather than retrying it.";

    private const string Approve = "approve";
    private const string Reject = "reject";

    private readonly CodeSpaceDbContext _db;
    private readonly IToolApprovalWaiterRegistry _waiters;
    private readonly ILogger<ToolCallApprovalResolver> _logger;

    public ToolCallApprovalResolver(CodeSpaceDbContext db, IToolApprovalWaiterRegistry waiters, ILogger<ToolCallApprovalResolver> logger)
    {
        _db = db;
        _waiters = waiters;
        _logger = logger;
    }

    public async Task<ActionResumeResult> ResolveByTokenAsync(string token, string responseKey, Guid actorUserId, Guid teamId, CancellationToken ct)
    {
        // Team-scoped fresh + untracked read (defense-in-depth — a leaked/cross-team token finds nothing). The partial
        // index on approval_token (migration 0049) keeps this lookup tiny.
        var row = await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.ApprovalToken == token && l.TeamId == teamId)
            .Select(l => new ParkedRow(l.Id, l.Status, l.AgentRunId, l.ApprovalTarget))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (row == null)
        {
            _logger.LogDebug("Tool-call approval resolve: no parked approval matches the token in team {TeamId} — caller records the click", teamId);
            return ActionResumeResult.NoWait;
        }

        if (row.Status != ToolCallLedgerStatus.AwaitingApproval)
        {
            _logger.LogDebug("Tool-call approval resolve: ledger {LedgerId} is {Status}, not AwaitingApproval — rejecting the late click", row.Id, row.Status);
            return ActionResumeResult.AlreadyResolved;
        }

        // The approval card only ever emits "approve" / "reject"; any other key is a fail-safe that records the response
        // in the living thread WITHOUT resolving the approval — it must never approve or fail the row.
        return responseKey switch
        {
            Reject => await RejectAsync(row, teamId, actorUserId, ct).ConfigureAwait(false),
            Approve => await ApproveAsync(row.Id, teamId, actorUserId, ct).ConfigureAwait(false),
            _ => NoWaitForUnknownKey(row.Id, responseKey),
        };
    }

    // Reject — status-guarded CAS AwaitingApproval → Failed (a legal transition). No side effect to run. The reviewer is
    // recorded in last_modified_by, not in the error: the error is what the model is replayed. The approved_at == null
    // guard mirrors ApproveAsync's: a card only serializes its own clicks, so a reject on a second card after an approve
    // must lose here rather than fail an approved call before it runs.
    private async Task<ActionResumeResult> RejectAsync(ParkedRow row, Guid teamId, Guid actorUserId, CancellationToken ct)
    {
        if (await FailUndecidedAsync([row.Id], teamId, actorUserId, ct).ConfigureAwait(false) == 0) return ActionResumeResult.AlreadyResolved;

        _waiters.TrySignal(row.Id, ToolApprovalOutcome.Rejected);

        _logger.LogInformation("Tool-call approval rejected. LedgerId={LedgerId} By={ActorUserId}", row.Id, actorUserId);

        await RejectSiblingsAsync(row, teamId, actorUserId, ct).ConfigureAwait(false);

        return ActionResumeResult.Resumed;
    }

    // The rejection reaches every other undecided call of the run on the same target — one parked an instant apart on
    // another connection, or before a target held one card: rejecting one card must not leave an approvable twin. An
    // approved sibling is left to the handler, which re-checks the target before it runs one; a later fresh call on the
    // target is denied at park.
    private async Task RejectSiblingsAsync(ParkedRow row, Guid teamId, Guid actorUserId, CancellationToken ct)
    {
        if (row.ApprovalTarget is null) return;

        var siblings = await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.AgentRunId == row.AgentRunId && l.TeamId == teamId && l.ApprovalTarget == row.ApprovalTarget && l.Id != row.Id && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null)
            .Select(l => l.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        if (siblings.Count == 0) return;

        await FailUndecidedAsync(siblings, teamId, actorUserId, ct).ConfigureAwait(false);

        foreach (var sibling in siblings) _waiters.TrySignal(sibling, ToolApprovalOutcome.Rejected);

        _logger.LogInformation("Tool-call approval rejection reached {Count} other call(s) of run {RunId} on the same target. LedgerId={LedgerId}", siblings.Count, row.AgentRunId, row.Id);
    }

    /// <summary>The status-guarded reject CAS over <paramref name="ledgerIds"/>: each still-undecided one fails with <see cref="RejectedError"/>. Returns how many it failed.</summary>
    private async Task<int> FailUndecidedAsync(IReadOnlyList<Guid> ledgerIds, Guid teamId, Guid actorUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        return await _db.ToolCallLedger
            .Where(l => ledgerIds.Contains(l.Id) && l.TeamId == teamId && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ToolCallLedgerStatus.Failed)
                .SetProperty(l => l.Error, RejectedError)
                .SetProperty(l => l.LastModifiedDate, now)
                .SetProperty(l => l.LastModifiedBy, actorUserId), ct)
            .ConfigureAwait(false);
    }

    // Approve — status-guarded CAS that STAMPS the decision WITHOUT changing status (the row stays AwaitingApproval; the
    // handler flips it to terminal once it runs the side effect). The approved_at == null guard makes a concurrent
    // approve idempotent: exactly one stamp wins, the loser sees affected == 0 → AlreadyResolved.
    private async Task<ActionResumeResult> ApproveAsync(Guid ledgerId, Guid teamId, Guid actorUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var affected = await _db.ToolCallLedger
            .Where(l => l.Id == ledgerId && l.TeamId == teamId && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ApprovedByUserId, actorUserId)
                .SetProperty(l => l.ApprovedAt, now)
                .SetProperty(l => l.LastModifiedDate, now)
                .SetProperty(l => l.LastModifiedBy, actorUserId), ct)
            .ConfigureAwait(false);

        if (affected == 0) return ActionResumeResult.AlreadyResolved;

        _waiters.TrySignal(ledgerId, ToolApprovalOutcome.Approved);

        _logger.LogInformation("Tool-call approval approved. LedgerId={LedgerId} By={ActorUserId}", ledgerId, actorUserId);
        return ActionResumeResult.Resumed;
    }

    /// <summary>The parked row a click names: what the reject needs to reach the row's siblings on the same target.</summary>
    private sealed record ParkedRow(Guid Id, ToolCallLedgerStatus Status, Guid AgentRunId, string? ApprovalTarget);

    private ActionResumeResult NoWaitForUnknownKey(Guid ledgerId, string responseKey)
    {
        _logger.LogDebug("Tool-call approval resolve: unknown responseKey {ResponseKey} for ledger {LedgerId} — recording without resolving (fail-safe, never approves)", responseKey, ledgerId);
        return ActionResumeResult.NoWait;
    }
}
