using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Budget;
using CodeSpace.Messages.Exceptions;
using Serilog;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// A model call could not be admitted under the run's cost cap — the run must STOP on its cost bound, never park
/// and retry (the ledger will refuse forever). Deliberately NOT an <c>LlmApiException</c>: the park-don't-die
/// transient-fault path must never swallow a budget refusal as an infra hiccup.
/// </summary>
public sealed class LlmBudgetExceededException(string kind, decimal committedUsd, decimal capUsd, string? reason = null, BudgetCapGrain? refusedGrain = null)
    : InvalidOperationException($"Model call '{kind}' refused by the budget ledger: committed ${committedUsd:0.####} against cap ${capUsd:0.####}. {reason}".TrimEnd()), Messages.Failures.IFailure
{
    public string Kind { get; } = kind;
    public decimal CommittedUsd { get; } = committedUsd;
    public decimal CapUsd { get; } = capUsd;

    /// <summary>The ledger's own refusal reason, e.g. naming a TEAM cap by value — never the run's, when a different grain refused.</summary>
    public string? Reason { get; } = reason;

    /// <summary>WHICH cap refused this call — null when the refusal didn't come from a named grain (e.g. a replay mismatch). Distinguishes "this run exhausted its own cap" from "the team/deployment ceiling refused it".</summary>
    public BudgetCapGrain? RefusedGrain { get; } = refusedGrain;

    // The failure taxonomy (#1353): a spent run budget is Exhausted — the caller's remedy is a bigger cap or a
    // narrower goal, never a retry of the same call.
    Messages.Failures.FailureKind Messages.Failures.IFailure.Kind => Messages.Failures.FailureKind.Exhausted;
    string Messages.Failures.IFailure.Code => Messages.Failures.FailureCodes.RunBudgetExhausted;
    string? Messages.Failures.IFailure.ClientMessage => "The run's cost cap is spent.";
}

/// <summary>
/// Reserves an admission estimate before buffered and structured provider calls, then records complete known
/// usage or retains an uncertain claim. Only scopes carrying both ledger and cap are guarded — a declared cap with
/// no cap VALUE (<see cref="LlmCallScope.CapUsd"/> null) stays the deliberate no-cap-configured no-op it always
/// was. The prompt heuristic and default output estimate are not wire upper bounds; provider-internal retries and
/// direct streaming require their own request admission before this can be described as a hard cap on the bill.
///
/// <para><b>P15-5a fail-LOUD:</b> a scope with no <see cref="LlmCallScope.Budget"/> ledger wired at all is no
/// longer a silent, unlogged passthrough (the old fail-open let a plane spend past its launch's cap forever) — it
/// throws <see cref="UnscopedModelCallException"/>, a programming-error signal for a plane that forgot to thread
/// its scope. A plane that legitimately has no launch marks itself <see cref="LlmCallScope.Unbudgeted"/> instead,
/// which still passes through but is LOGGED and, when a ledger is carried, recorded under an "unbudgeted:" kind.</para>
/// </summary>
public static class LlmBudgetGuard
{
    /// <summary>Legacy output-token estimate for admission when the request has no explicit bound. This is not a wire ceiling.</summary>
    public const int DefaultMaxOutputTokensEstimate = 8192;

    /// <summary>Every llm reservation carries this TTL — generously past any real call's own HTTP timeout, so only a reservation ORPHANED by a worker teardown between reserve and settle ever reaches it. The expiry sweep then moves it to Indeterminate and the settlement sweep reconciles it pessimistically; without a deadline it would sit live forever, invisibly holding headroom against every later call of a reclaimed run. Pinned by test.</summary>
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromMinutes(30);

    public static async Task<T> GuardedAsync<T>(LlmCallScope? scope, string model, string? systemPrompt, string? userPrompt, int? maxOutputTokens, Func<CancellationToken, Task<T>> call, Func<T, decimal?> actualUsd, CancellationToken cancellationToken)
    {
        if (scope is { UnbudgetedReason: { } reason })
            return await UnbudgetedPassthroughAsync(scope, reason, model, systemPrompt, userPrompt, maxOutputTokens, call, actualUsd, cancellationToken).ConfigureAwait(false);

        if (scope is not { Budget: { } budget })
            throw new UnscopedModelCallException(scope?.Kind ?? "(unscoped)", model);

        if (scope.CapUsd is not { } capUsd) return await call(cancellationToken).ConfigureAwait(false);

        var estimate = EstimateUsd(model, systemPrompt, userPrompt, maxOutputTokens, scope.ModelPrices);

        // D1 fail-CLOSED: this scope carries a cap, so an unpriceable model is not merely "cost-unknown" — it is a cap
        // that cannot be enforced. Passing the call through (the old fail-open) let every Codex/OpenAI/Custom-pool
        // brain call sum to $0 and spend past the operator's cap forever. Refuse, naming the model + the remedy.
        // A brain call that FAILED OVER to another pool row (#1737/#1738) re-enters this guard under the SUCCESSOR's
        // own name, so each candidate is judged on its own price — never admitted on the first pick's.
        if (estimate is null) throw new UnpricedModelUnderCapException(model, capUsd, $"model call '{scope.Kind}'");

        // One reservation per PHYSICAL call (a replayed turn makes a new call and spends real money again), scoped
        // under the run: the ledger's advisory lock serializes concurrent reserves, so the second caller sees the
        // first's headroom claim. Actual spend can exceed this estimate; no wire-bound guarantee is implied.
        var scopeKey = $"{scope.Kind}:{Guid.NewGuid():N}";
        var kind = $"llm:{scope.Kind}";

        var admission = await budget.ReserveAsync(scope.RunId, scope.TeamId, kind, scopeKey, estimate.Value, capUsd, priceVersion: "realized-v1", parentReservationId: null, expiresAt: DateTimeOffset.UtcNow.Add(ReservationTtl), cancellationToken).ConfigureAwait(false);

        if (!admission.Admitted) throw new LlmBudgetExceededException(scope.Kind, admission.CommittedUsd, capUsd, admission.Reason, admission.RefusedGrain);
        if (admission.IsReplay) throw new LlmBudgetExceededException(scope.Kind, admission.CommittedUsd, capUsd, "An existing logical reservation cannot authorize another provider request.");

        try
        {
            var completion = await call(cancellationToken).ConfigureAwait(false);

            await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd(completion), cancellationToken).ConfigureAwait(false);

            return completion;
        }
        catch (LlmBudgetExceededException) { throw; }
        catch (UnpricedModelUnderCapException) { throw; }
        catch
        {
            // The call itself failed — the actual spend is unknowable here; the ledger's null-actual settle is
            // uncertain: it retains the reserve without inventing an actual bill.
            await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd: null, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// P15-5a: the call always proceeds — an <see cref="LlmCallScope.Unbudgeted"/> plane declared it has no launch
    /// cap to meter against. Never a silent passthrough though: this LOGS the plane + reason by name, and — when
    /// the scope also carries a <see cref="LlmCallScope.Budget"/> ledger — records the spend under an
    /// "unbudgeted:" kind (<see cref="BudgetKinds.UnbudgetedPrefix"/>) for observability. That kind is excluded
    /// from every committed-sum query, so it can never be admitted-against or eat into a DIFFERENT plane's real
    /// cap on the same run; an unpriceable model is never refused here either, since there is no cap to protect.
    /// </summary>
    private static async Task<T> UnbudgetedPassthroughAsync<T>(LlmCallScope scope, string reason, string model, string? systemPrompt, string? userPrompt, int? maxOutputTokens, Func<CancellationToken, Task<T>> call, Func<T, decimal?> actualUsd, CancellationToken cancellationToken)
    {
        Log.Warning("Model call {Kind} for model {Model} is Unbudgeted ({Reason}) — proceeding un-metered against any cap", scope.Kind, model, reason);

        if (scope.Budget is not { } budget) return await call(cancellationToken).ConfigureAwait(false);

        var kind = $"{BudgetKinds.UnbudgetedPrefix}{scope.Kind}";
        var scopeKey = $"{scope.Kind}:{Guid.NewGuid():N}";
        var estimate = EstimateUsd(model, systemPrompt, userPrompt, maxOutputTokens, scope.ModelPrices) ?? 0m;

        await ReserveQuietlyAsync(budget, scope, kind, scopeKey, estimate, cancellationToken).ConfigureAwait(false);

        try
        {
            var completion = await call(cancellationToken).ConfigureAwait(false);

            await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd(completion), cancellationToken).ConfigureAwait(false);

            return completion;
        }
        catch
        {
            await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd: null, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>An Unbudgeted observability record — never an admission gate (a null cap, so it can never refuse and never poisons a cap derived from this run's OTHER reservations), and a ledger failure here must never block or fault the call it is merely trying to describe.</summary>
    private static async Task ReserveQuietlyAsync(IBudgetLedger budget, LlmCallScope scope, string kind, string scopeKey, decimal estimateUsd, CancellationToken cancellationToken)
    {
        try { await budget.ReserveAsync(scope.RunId, scope.TeamId, kind, scopeKey, estimateUsd, capUsd: null, priceVersion: "realized-v1", parentReservationId: null, expiresAt: DateTimeOffset.UtcNow.Add(ReservationTtl), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* torn down — nothing to reconcile since there is no cap this reservation protects */ }
        catch { /* best-effort — see summary */ }
    }

    /// <summary>Legacy pre-call estimate: chars/3 input plus framing + the request's output bound or the committed default. Null when the model is unpriceable in EVERY table (per-row → env → built-in).</summary>
    internal static decimal? EstimateUsd(string model, string? systemPrompt, string? userPrompt, int? maxOutputTokens, IReadOnlyDictionary<string, Messages.Agents.ModelPrice>? rowPrices = null)
    {
        var inputTokens = (int)Math.Min(((systemPrompt?.Length ?? 0) + (userPrompt?.Length ?? 0)) / 3L + 64, int.MaxValue);
        var outputTokens = maxOutputTokens is > 0 ? maxOutputTokens.Value : DefaultMaxOutputTokensEstimate;

        return LlmUsageCost.Usd(model, new LlmUsage { InputTokens = inputTokens, OutputTokens = outputTokens }, rowPrices);
    }

    /// <summary>Preserve the provider outcome if accounting persistence fails. Its existing reservation retains the admission estimate, not a guaranteed bound on the unknown bill; recovery and cost qualification must keep that distinction.</summary>
    private static async Task SettleQuietlyAsync(IBudgetLedger budget, LlmCallScope scope, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
    {
        try { await budget.SettleAsync(scope.RunId, scope.TeamId, kind, scopeKey, actualUsd, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* torn down — the expiry sweep reconciles the live reservation */ }
        catch { /* best-effort — see summary */ }
    }
}
