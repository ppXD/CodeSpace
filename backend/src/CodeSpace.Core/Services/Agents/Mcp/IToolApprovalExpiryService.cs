using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
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
///
/// <para>The reaper runs under the <see cref="TransactionalBehavior{TRequest,TResponse}"/> command transaction, so the
/// CAS only becomes visible to OTHER connections at the command's commit. The same-pod waiter wake + card mirror are
/// therefore deferred via <see cref="IPostCommitActions"/> so a woken handler — which re-reads the row on its OWN
/// connection — sees the COMMITTED <c>Expired</c> terminal and replays it (not the pre-commit <c>AwaitingApproval</c>,
/// which would lose the same-pod fast-path the design promises). Called outside a transaction (ad-hoc / tests), the
/// deferred action runs inline since the CAS already auto-committed.</para>
///
/// <para>Each row's wake and card mirror are best-effort, the two on their own. The drain swallows an action that throws,
/// one row at a time and without naming the row; a caller with no transaction runs them inline with nothing to swallow,
/// and there one card's failed mirror would cost every later row its wake and its mirror. So each step catches its own
/// failure and logs the ledger row: a failed mirror still leaves its call woken, and the next row woken and mirrored. The
/// row stands Expired either way; a card left Open is refused a click, the row being the authority.</para>
/// </summary>
public interface IToolApprovalExpiryService
{
    /// <summary>Expire every undecided approval past <paramref name="now"/>, then best-effort signal waiters + mirror cards (deferred until after the ambient command transaction commits). Returns the count durably expired (the count is the ledger CAS winners, not the best-effort follow-ups).</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class ToolApprovalExpiryService : IToolApprovalExpiryService, IScopedDependency
{
    private readonly IToolCallLedgerService _ledger;
    private readonly IToolApprovalWaiterRegistry _waiters;
    private readonly IMessageInteractionService _interactions;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<ToolApprovalExpiryService> _logger;

    public ToolApprovalExpiryService(IToolCallLedgerService ledger, IToolApprovalWaiterRegistry waiters, IMessageInteractionService interactions, IPostCommitActions postCommit, ILogger<ToolApprovalExpiryService> logger)
    {
        _ledger = ledger;
        _waiters = waiters;
        _interactions = interactions;
        _postCommit = postCommit;
        _logger = logger;
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The durable CAS is the authority + the count we return — the row is Expired regardless of the follow-ups.
        var expired = await _ledger.ExpireStaleApprovalsAsync(now, cancellationToken).ConfigureAwait(false);

        // Defer the per-row signal + card mirror until AFTER the command transaction commits (the CAS isn't visible to
        // a woken handler's own connection until then). Outside a transaction this runs inline (the CAS auto-committed).
        foreach (var row in expired)
            await _postCommit.RunAfterCommitAsync(ct => ResolveAsync(row, ct), cancellationToken).ConfigureAwait(false);

        return expired.Count;
    }

    private async Task ResolveAsync(ExpiredToolApproval row, CancellationToken cancellationToken)
    {
        WakeQuietly(row);

        await MirrorQuietlyAsync(row, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Tool call approval expired. LedgerId={LedgerId} TeamId={TeamId}", row.LedgerId, row.TeamId);
    }

    // Best-effort SAME-POD fast-path: wake a handler blocked on THIS pod immediately. It re-reads the now-committed
    // Expired terminal on its own connection and replays it. Cross-pod it harmlessly returns false — and that's
    // fine: the durable Expired row + the blocked call's bounded-elapse → pending-ticket → a re-call that replays
    // the Expired terminal IS the cross-pod guarantee. No wake, no decision lost. A wake that throws costs its card
    // nothing: the mirror is its own step.
    private void WakeQuietly(ExpiredToolApproval row)
    {
        try
        {
            _waiters.TrySignal(row.LedgerId, ToolApprovalOutcome.Expired);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tool call approval {LedgerId} expired, but waking its call failed; the call reads the Expired terminal when it next looks", row.LedgerId);
        }
    }

    // Best-effort + idempotent: mirror the approval card to timed-out (no-ops if a human already resolved it, or if no
    // card was ever posted). The ledger row is the authority; this is its display mirror. A failed mirror leaves nothing
    // tracked on the context (the interaction service forgets the card whose save failed), so the next row's mirror is
    // not the one that writes it.
    private async Task MirrorQuietlyAsync(ExpiredToolApproval row, CancellationToken cancellationToken)
    {
        if (row.ApprovalMessageId is not { } messageId) return;

        try
        {
            await _interactions.MarkTimedOutAsync(messageId, "expired", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Tool call approval {LedgerId} expired, but mirroring its card failed; the ledger row stands as the verdict", row.LedgerId);
        }
    }
}
