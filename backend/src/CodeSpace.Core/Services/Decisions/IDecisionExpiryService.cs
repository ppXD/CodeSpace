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
/// <para>The wake + card mirror go through <see cref="IPostCommitActions"/> so a woken handler — re-reading the row on its
/// OWN connection — sees the COMMITTED <c>Succeeded</c> terminal, not the pre-commit <c>AwaitingApproval</c>. The sweep's
/// command is non-transactional, so each decision's CAS commits on its own, and its follow-ups run inline as the sweep
/// reaches it — right after that CAS, before the sweep moves on, so no later decision's fault can cost them; only a caller
/// inside a transaction of its own defers them to its commit. With no drain to swallow a failure they are best-effort
/// here, the wake and the mirror each on its own: one decision's failed card mirror still leaves it woken and the next
/// decision woken and mirrored.</para>
/// </summary>
public interface IDecisionExpiryService
{
    /// <summary>Apply the configured default to every undecided decision past <paramref name="now"/>, best-effort waking each one's waiter and mirroring its card right after its default commits — deferred to the commit only when called inside a caller's transaction. Returns the count durably defaulted (the ledger-CAS winners; rows without a default are left Pending for a human).</summary>
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
        var defaulted = 0;

        // The sweep hands each default back the moment its CAS autocommits, so the per-row signal + card mirror run right
        // then, before it moves on. Only inside a caller's transaction do they wait for its commit — until then a woken
        // handler's own connection would still read the row AwaitingApproval.
        await foreach (var row in _ledger.ExpireStaleDecisionsAsync(now, cancellationToken).ConfigureAwait(false))
        {
            defaulted++;
            await _postCommit.RunAfterCommitAsync(ct => ResolveAsync(row, ct), cancellationToken).ConfigureAwait(false);
        }

        return defaulted;
    }

    private async Task ResolveAsync(TimedOutDecision row, CancellationToken cancellationToken)
    {
        WakeQuietly(row);

        await MirrorQuietlyAsync(row, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Decision defaulted on timeout. LedgerId={LedgerId} TeamId={TeamId}", row.LedgerId, row.TeamId);
    }

    // Best-effort SAME-POD fast-path: wake a decision call blocked on THIS pod so it re-reads the now-committed Succeeded
    // terminal + its default answer (Approved — the decision was ANSWERED by timeout, not expired; the row is Succeeded,
    // not an error terminal). Cross-pod it harmlessly returns false — the durable Succeeded row + the blocked call's
    // bounded-elapse → re-call that replays the terminal IS the cross-pod guarantee. A wake that throws costs its card
    // nothing: the mirror is its own step.
    private void WakeQuietly(TimedOutDecision row)
    {
        try
        {
            _waiters.TrySignal(row.LedgerId, ToolApprovalOutcome.Approved);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Decision {LedgerId} was defaulted on timeout, but waking its call failed; the call reads the default when it next looks", row.LedgerId);
        }
    }

    // Best-effort + idempotent: mirror the decision card to timed-out (no-ops if a human already resolved it, or if no
    // card was ever posted). The ledger row is the authority; this is its display mirror.
    private async Task MirrorQuietlyAsync(TimedOutDecision row, CancellationToken cancellationToken)
    {
        if (row.ApprovalMessageId is not { } messageId) return;

        try
        {
            await _interactions.MarkTimedOutAsync(messageId, "timed out", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Decision {LedgerId} was defaulted on timeout, but mirroring its card failed; the ledger row stands as the answer", row.LedgerId);
        }
    }
}
