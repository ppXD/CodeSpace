using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Budget;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// A model call could not be admitted under the run's cost cap — the run must STOP on its cost bound, never park
/// and retry (the ledger will refuse forever). Deliberately NOT an <c>LlmApiException</c>: the park-don't-die
/// transient-fault path must never swallow a budget refusal as an infra hiccup.
/// </summary>
public sealed class LlmBudgetExceededException(string kind, decimal committedUsd, decimal capUsd)
    : InvalidOperationException($"Model call '{kind}' refused by the budget ledger: committed ${committedUsd:0.####} against cap ${capUsd:0.####}."), Messages.Failures.IFailure
{
    public string Kind { get; } = kind;
    public decimal CommittedUsd { get; } = committedUsd;
    public decimal CapUsd { get; } = capUsd;

    // The failure taxonomy (#1353): a spent run budget is Exhausted — the caller's remedy is a bigger cap or a
    // narrower goal, never a retry of the same call.
    Messages.Failures.FailureKind Messages.Failures.IFailure.Kind => Messages.Failures.FailureKind.Exhausted;
    string Messages.Failures.IFailure.Code => Messages.Failures.FailureCodes.RunBudgetExhausted;
    string? Messages.Failures.IFailure.ClientMessage => "The run's cost cap is spent.";
}

/// <summary>
/// W-hard: the ATOMIC half of the brain-plane cost bound, applied at the ONE funnel every model call already rides
/// (the recording decorator family — all faces route their inner call through <see cref="GuardedAsync"/>). The
/// per-turn bound (<c>SupervisorBounds</c> over realized spend) checks BETWEEN decisions; this closes the
/// intra-turn window by reserving pessimistically BEFORE each call and settling at the actual usage after — the
/// ledger's <c>settled + live ≤ cap</c> invariant means concurrent calls can never jointly overshoot. Active ONLY
/// when the pushed scope carries a ledger + cap (today the supervisor's decision scope); every other call — and an
/// unpriceable model, mirroring the cost plane's fail-open posture — passes through byte-identical.
/// </summary>
public static class LlmBudgetGuard
{
    /// <summary>Pessimistic output-token assumption when the request does not bound it — over-reserving briefly is safe (the settle corrects immediately); under-reserving is the overshoot this guard exists to prevent. Pinned by test.</summary>
    public const int DefaultMaxOutputTokensEstimate = 8192;

    /// <summary>Every llm reservation carries this TTL — generously past any real call's own HTTP timeout, so only a reservation ORPHANED by a worker teardown between reserve and settle ever reaches it. The expiry sweep then moves it to Indeterminate and the settlement sweep reconciles it pessimistically; without a deadline it would sit live forever, invisibly holding headroom against every later call of a reclaimed run. Pinned by test.</summary>
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromMinutes(30);

    public static async Task<T> GuardedAsync<T>(LlmCallScope? scope, string model, string? systemPrompt, string? userPrompt, int? maxOutputTokens, Func<CancellationToken, Task<T>> call, Func<T, decimal?> actualUsd, CancellationToken cancellationToken)
    {
        if (scope is not { Budget: { } budget, CapUsd: { } capUsd }) return await call(cancellationToken).ConfigureAwait(false);

        var estimate = EstimateUsd(model, systemPrompt, userPrompt, maxOutputTokens);

        if (estimate is null) return await call(cancellationToken).ConfigureAwait(false);   // unpriceable model — fail-open, like the cost plane

        // One reservation per PHYSICAL call (a replayed turn makes a new call and spends real money again), scoped
        // under the run: the ledger's advisory lock serializes concurrent reserves, so the second caller sees the
        // first's headroom claim — the invariant holds DURING the calls, not just between decisions.
        var scopeKey = $"{scope.Kind}:{Guid.NewGuid():N}";
        var kind = $"llm:{scope.Kind}";

        var admission = await budget.ReserveAsync(scope.RunId, scope.TeamId, kind, scopeKey, estimate.Value, capUsd, priceVersion: "realized-v1", parentReservationId: null, expiresAt: DateTimeOffset.UtcNow.Add(ReservationTtl), cancellationToken).ConfigureAwait(false);

        if (!admission.Admitted) throw new LlmBudgetExceededException(scope.Kind, admission.CommittedUsd, admission.CapUsd);

        try
        {
            var completion = await call(cancellationToken).ConfigureAwait(false);

            await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd(completion), cancellationToken).ConfigureAwait(false);

            return completion;
        }
        catch (LlmBudgetExceededException) { throw; }
        catch
        {
            // The call itself failed — the actual spend is unknowable here; the ledger's null-actual settle is
            // pessimistic (settles AT the reserve), which is the only safe direction.
            await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd: null, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Pessimistic pre-call estimate: chars/3 input (never chars/4 — CJK undercounts) + the request's output bound or the committed default. Null when the model is unpriceable.</summary>
    internal static decimal? EstimateUsd(string model, string? systemPrompt, string? userPrompt, int? maxOutputTokens)
    {
        var inputTokens = (int)Math.Min(((systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0)) / 3L + 64, int.MaxValue);
        var outputTokens = maxOutputTokens is > 0 ? maxOutputTokens.Value : DefaultMaxOutputTokensEstimate;

        return AgentCostPricing.CostUsd(model, inputTokens, outputTokens);
    }

    /// <summary>Settlement is bookkeeping — it must never fault the completed call (an unsettled reservation sits live until the expiry sweep reconciles it, pessimistically holding headroom: safe in the only direction that matters).</summary>
    private static async Task SettleQuietlyAsync(IBudgetLedger budget, LlmCallScope scope, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
    {
        try { await budget.SettleAsync(scope.RunId, scope.TeamId, kind, scopeKey, actualUsd, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* torn down — the expiry sweep reconciles the live reservation */ }
        catch { /* best-effort — see summary */ }
    }
}
