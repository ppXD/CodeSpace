using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Decisions;

/// <summary>
/// Orchestrates the durable timeout-default of stranded agent-grain decisions past their deadline (Decision substrate
/// D5b — AC4 never-hang). The ledger CAS is the AUTHORITY (<see cref="IToolCallLedgerService.ExpireStaleDecisionsAsync"/>
/// answers each overdue decision with its configured default → <c>Succeeded</c>), then for each one this best-effort
/// (a) wakes any in-process blocked decision call so it reads the now-<c>Succeeded</c> terminal + the default answer, and
/// (b) mirrors the decision card to timed-out. The <see cref="ToolApprovalExpiryService"/> analogue for the decision
/// grain — same post-commit discipline, but the wake outcome is <c>Approved</c> (the decision WAS answered, by the
/// timeout default), NOT <c>Expired</c> (the approval grain's "no decision" terminal — which the blocked call would
/// surface as an error). The recurring reaper job dispatches a command whose thin handler (Rule 16) calls this.
///
/// <para>The wake + card mirror are deferred via <see cref="IPostCommitActions"/> so a woken handler — re-reading the row
/// on its OWN connection — sees the COMMITTED <c>Succeeded</c> terminal, not the pre-commit <c>AwaitingApproval</c>. Called
/// outside a transaction (ad-hoc / tests), the deferred action runs inline since the CAS already auto-committed.</para>
/// </summary>
public interface IDecisionExpiryService
{
    /// <summary>Apply the configured default to every undecided decision past <paramref name="now"/>, then best-effort wake waiters + mirror cards (deferred until the ambient command transaction commits). Returns the count durably defaulted (the ledger-CAS winners; rows without a default are left Pending for a human).</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class DecisionExpiryService : IDecisionExpiryService, IScopedDependency
{
    private readonly IToolCallLedgerService _ledger;
    private readonly IToolApprovalWaiterRegistry _waiters;
    private readonly IMessageInteractionService _interactions;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<DecisionExpiryService> _logger;

    public DecisionExpiryService(IToolCallLedgerService ledger, IToolApprovalWaiterRegistry waiters, IMessageInteractionService interactions, IPostCommitActions postCommit, ILogger<DecisionExpiryService> logger)
    {
        _ledger = ledger;
        _waiters = waiters;
        _interactions = interactions;
        _postCommit = postCommit;
        _logger = logger;
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The durable answer CAS is the authority + the count we return — the row is Succeeded-by-default regardless of the follow-ups.
        var timedOut = await _ledger.ExpireStaleDecisionsAsync(now, cancellationToken).ConfigureAwait(false);

        // Defer the per-row signal + card mirror until AFTER the command transaction commits (the CAS isn't visible to a
        // woken handler's own connection until then). Outside a transaction this runs inline (the CAS auto-committed).
        foreach (var row in timedOut)
            await _postCommit.RunAfterCommitAsync(ct => ResolveAsync(row, ct), cancellationToken).ConfigureAwait(false);

        return timedOut.Count;
    }

    private async Task ResolveAsync(TimedOutDecision row, CancellationToken cancellationToken)
    {
        // Best-effort SAME-POD fast-path: wake a decision call blocked on THIS pod so it re-reads the now-committed
        // Succeeded terminal + its default answer (Approved — the decision was ANSWERED by timeout, not expired; the row
        // is Succeeded, not an error terminal). Cross-pod it harmlessly returns false — the durable Succeeded row + the
        // blocked call's bounded-elapse → re-call that replays the terminal IS the cross-pod guarantee.
        _waiters.TrySignal(row.LedgerId, ToolApprovalOutcome.Approved);

        // Best-effort + idempotent: mirror the decision card to timed-out (no-ops if a human already resolved it, or if no
        // card was ever posted). The ledger row is the authority; this is its display mirror.
        if (row.ApprovalMessageId is { } messageId)
            await _interactions.MarkTimedOutAsync(messageId, "timed out", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Decision defaulted on timeout and mirrored. LedgerId={LedgerId} TeamId={TeamId}", row.LedgerId, row.TeamId);
    }
}
