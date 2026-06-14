using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// Orchestrates the durable expiry of undecided tool-call approvals past their deadline (item D3). Owns the
/// cross-cutting follow-ups around <see cref="IToolCallLedgerService.ExpireStaleApprovalsAsync"/>: the ledger CAS is
/// the AUTHORITY (it durably flips each row <c>AwaitingApproval → Expired</c>), then for each expired row this best-effort
/// (a) wakes any in-process blocked handler waiter so it resumes immediately, and (b) mirrors the approval card to
/// timed-out. The recurring reaper job dispatches a command whose thin handler (Rule 16) calls this — the handler holds
/// no logic, this service holds it all.
/// </summary>
public interface IToolApprovalExpiryService
{
    /// <summary>Expire every undecided approval past <paramref name="now"/>, then best-effort signal waiters + mirror cards. Returns the count durably expired (the count is the ledger CAS winners, not the best-effort follow-ups).</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class ToolApprovalExpiryService : IToolApprovalExpiryService, IScopedDependency
{
    private readonly IToolCallLedgerService _ledger;
    private readonly IToolApprovalWaiterRegistry _waiters;
    private readonly IMessageInteractionService _interactions;
    private readonly ILogger<ToolApprovalExpiryService> _logger;

    public ToolApprovalExpiryService(IToolCallLedgerService ledger, IToolApprovalWaiterRegistry waiters, IMessageInteractionService interactions, ILogger<ToolApprovalExpiryService> logger)
    {
        _ledger = ledger;
        _waiters = waiters;
        _interactions = interactions;
        _logger = logger;
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The durable CAS is the authority + the count we return — the row is Expired regardless of the follow-ups.
        var expired = await _ledger.ExpireStaleApprovalsAsync(now, cancellationToken).ConfigureAwait(false);

        foreach (var row in expired)
            await ResolveAsync(row, cancellationToken).ConfigureAwait(false);

        return expired.Count;
    }

    private async Task ResolveAsync(ExpiredToolApproval row, CancellationToken cancellationToken)
    {
        // Best-effort SAME-POD fast-path: wake a handler blocked on THIS pod immediately. Cross-pod it harmlessly
        // returns false — and that's fine: the durable Expired row + the blocked call's bounded-elapse → pending-ticket
        // → a re-call that replays the Expired terminal IS the cross-pod guarantee. No wake, no decision lost.
        _waiters.TrySignal(row.LedgerId, ToolApprovalOutcome.Expired);

        // Best-effort + idempotent: mirror the approval card to timed-out (no-ops if a human already resolved it, or if
        // no card was ever posted). The ledger row is the authority; this is its display mirror.
        if (row.ApprovalMessageId is { } messageId)
            await _interactions.MarkTimedOutAsync(messageId, "expired", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Tool call approval expired and mirrored. LedgerId={LedgerId} TeamId={TeamId}", row.LedgerId, row.TeamId);
    }
}
