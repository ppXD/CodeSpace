using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Exceptions;
using Serilog;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// A model call could not be admitted under the run's cost cap — the run must STOP on its cost bound, never park
/// and retry (the ledger will refuse forever). Deliberately NOT an <c>LlmApiException</c>: the park-don't-die
/// transient-fault path must never swallow a budget refusal as an infra hiccup.
/// </summary>
public sealed class LlmBudgetExceededException(string kind, decimal committedUsd, decimal capUsd, string? reason = null)
    : InvalidOperationException($"Model call '{kind}' refused by the budget ledger: committed ${committedUsd:0.####} against cap ${capUsd:0.####}. {reason}".TrimEnd()), Messages.Failures.IFailure
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
/// Reserves an admission estimate before buffered and structured provider calls, then records complete known
/// usage or retains an uncertain claim. A scope carrying a cap VALUE is admitted against it; a scope carrying a
/// ledger but NO cap value records an "unbudgeted:" observability row instead of enforcing anything. The prompt
/// heuristic and default output estimate are not wire upper bounds; provider-internal retries and direct streaming
/// require their own request admission before this can be described as a hard cap on the bill.
///
/// <para><b>P15-5a fail-LOUD:</b> a scope with no <see cref="LlmCallScope.Budget"/> ledger wired at all is no
/// longer a silent, unlogged passthrough (the old fail-open let a plane spend past its launch's cap forever) — it
/// throws <see cref="UnscopedModelCallException"/>, a programming-error signal for a plane that forgot to thread
/// its scope. A plane that legitimately has no launch marks itself <see cref="LlmCallScope.Unbudgeted"/> instead,
/// which still passes through but is LOGGED and, when a ledger is carried, recorded under an "unbudgeted:" kind.</para>
///
/// <para><b>Every call is admitted or recorded:</b> a BUDGETED scope whose cap value is null used to return
/// <c>await call(...)</c> directly — row-less and unlogged, the one silent passthrough left standing behind this
/// class's own "never a silent passthrough" claim. It now takes the same Unbudgeted passthrough under
/// <see cref="NoRunCapReason"/>, so a reader asking "what did this run spend on models" is never answered by an
/// absence. Enforcement is unchanged: an "unbudgeted:" row is excluded from every committed sum, so no capped
/// plane's headroom moves.</para>
/// </summary>
public static class LlmBudgetGuard
{
    /// <summary>Legacy output-token estimate for admission when the request has no explicit bound. This is not a wire ceiling.</summary>
    public const int DefaultMaxOutputTokensEstimate = 8192;

    /// <summary>The Unbudgeted reason a BUDGETED scope with no cap VALUE records under — an operator who configured no ceiling on an otherwise-instrumented launch. Pinned by test: it is the phrase an operator reads on the row explaining why their call was never metered.</summary>
    public const string NoRunCapReason = "run has no MaxCostUsd";

    /// <summary>Every llm reservation carries this TTL — generously past any real call's own HTTP timeout, so only a reservation ORPHANED by a worker teardown between reserve and settle ever reaches it. The expiry sweep then moves it to Indeterminate and the settlement sweep reconciles it pessimistically; without a deadline it would sit live forever, invisibly holding headroom against every later call of a reclaimed run. Pinned by test.</summary>
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromMinutes(30);

    public static async Task<T> GuardedAsync<T>(LlmCallScope? scope, string model, string? systemPrompt, string? userPrompt, int? maxOutputTokens, Func<CancellationToken, Task<T>> call, Func<T, decimal?> actualUsd, CancellationToken cancellationToken)
    {
        if (scope is { UnbudgetedReason: { } reason })
            return await UnbudgetedPassthroughAsync(scope, reason, model, systemPrompt, userPrompt, maxOutputTokens, call, actualUsd, cancellationToken).ConfigureAwait(false);

        if (scope is not { Budget: { } budget })
            throw new UnscopedModelCallException(scope?.Kind ?? "(unscoped)", model);

        if (scope.CapUsd is not { } capUsd)
            return await UnbudgetedPassthroughAsync(scope, NoRunCapReason, model, systemPrompt, userPrompt, maxOutputTokens, call, actualUsd, cancellationToken).ConfigureAwait(false);

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

        if (!admission.Admitted) throw new LlmBudgetExceededException(scope.Kind, admission.CommittedUsd, capUsd, admission.Reason);
        if (admission.IsReplay) throw new LlmBudgetExceededException(scope.Kind, admission.CommittedUsd, capUsd, "An existing logical reservation cannot authorize another provider request.");

        try
        {
            var completion = await call(cancellationToken).ConfigureAwait(false);

            await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd(completion), cancellationToken).ConfigureAwait(false);

            return completion;
        }
        catch (LlmBudgetExceededException) { throw; }
        catch (UnpricedModelUnderCapException) { throw; }
        catch (Exception ex)
        {
            // The call failed, so no usage was observed here in EITHER direction — what differs is whether the
            // provider could have billed anyway (see ObservedNoSpend). A transport failure that never produced a
            // completion RELEASES its headroom; anything ambiguous keeps the pessimistic null-actual settle.
            if (ObservedNoSpend(ex))
                await ReleaseQuietlyAsync(budget, scope, kind, scopeKey, cancellationToken).ConfigureAwait(false);
            else
                await SettleQuietlyAsync(budget, scope, kind, scopeKey, actualUsd: null, cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Whether a THROWN model call proves nothing was billed, so its admission estimate must go back to the cap.
    ///
    /// <para>WHY this branch exists: a null-actual settle lands <c>Indeterminate</c>, which HOLDS the estimate
    /// forever (<c>ReconcileDanglingAsync</c> keeps holding it, and no fact source ever settles an <c>llm:</c>
    /// row). A 429 / format-fault storm across failover hops — each hop its OWN reservation, since every successor
    /// re-enters this guard — therefore filled the run's cap with phantom estimates for calls that produced no
    /// tokens, and the next real call was refused with <see cref="LlmBudgetExceededException"/>. Releasing is
    /// recoverable in the one direction that matters: a late real receipt still supersedes a Released row
    /// (<c>BudgetLedger.SettleAsync</c> writes any state that is not already Settled), while a phantom hold is
    /// only ever corrected by the operator raising the cap.</para>
    ///
    /// <para><b>Release only when unbilled is PROVEN</b> — the CATEGORY is not that proof. The transport turns a
    /// client-side timeout and a mid-flight connection reset into the SAME <see cref="LlmErrorCategory.Transient"/>
    /// an HTTP 503 gets, and both of those were sent to a provider that generated (and billed) a completion we
    /// stopped listening to. Only two shapes prove no completion exists, and both are read from evidence the
    /// transport could not have faked:</para>
    /// <list type="bullet">
    ///   <item>An <see cref="LlmApiException"/> carrying an ERROR <see cref="LlmApiException.StatusCode"/> (4xx or
    ///         5xx): a status is a response, and an error response carries no completion ⇒ RELEASE, whatever the
    ///         category refined it to. A <see cref="LlmErrorCategory.Malformed"/> fault carries its 2xx status, so
    ///         it stays a hold on the same rule rather than on a special case.</item>
    ///   <item>An <see cref="HttpRequestException"/> whose socket error says the request never reached a server —
    ///         see <see cref="NeverReachedAServer"/> ⇒ RELEASE.</item>
    ///   <item>Everything else ⇒ hold: a status-less Transient (timeout, reset after send), a truncated body, a
    ///         cancellation, an untyped fault. Ambiguity is a hold, always.</item>
    /// </list>
    ///
    /// <para>Two vetoes outrank any proof found below them: a cancellation / timeout ANYWHERE in the chain (a
    /// caller that wrapped its own cancellation around a transport fault must not launder it into a release), and
    /// <see cref="LlmApiException.PriorBilledAttempt"/> — a bounded re-ask makes a SECOND physical call inside this
    /// one reservation, so a 429 on the re-ask proves nothing about the first request that already bought tokens.</para>
    /// </summary>
    internal static bool ObservedNoSpend(Exception thrown)
    {
        var proven = false;

        for (var e = thrown; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException or TimeoutException) return false;
            if (e is LlmApiException { PriorBilledAttempt: true }) return false;

            proven |= ProvesUnbilled(e);
        }

        return proven;
    }

    /// <summary>Whether THIS link of the chain is one of the two shapes that prove no completion was generated (see <see cref="ObservedNoSpend"/>).</summary>
    private static bool ProvesUnbilled(Exception e) => e switch
    {
        LlmApiException { StatusCode: >= 400 and <= 599 } => true,
        HttpRequestException http => NeverReachedAServer(http),
        _ => false,
    };

    /// <summary>
    /// Whether a transport fault proves the request never reached a server: the connection was refused, the host
    /// was unroutable, or DNS could not resolve it (<see cref="SocketError.HostNotFound"/>). Nothing was sent, so
    /// nothing could have been generated.
    ///
    /// <para>A reset, an abort mid-body or a truncated read is deliberately NOT here: those happen after the
    /// request was already on the wire, and the provider bills a completion it generated whether or not we managed
    /// to read it back.</para>
    /// </summary>
    private static bool NeverReachedAServer(HttpRequestException http)
    {
        for (var e = http.InnerException; e is not null; e = e.InnerException)
            if (e is SocketException socket)
                return socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NetworkUnreachable;

        return false;
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
            // Deliberately NOT the metered path's release branch: this row holds no cap's headroom (it is excluded
            // from every committed sum), so releasing would buy nothing — and the Room sums an Unbudgeted row's
            // estimate whatever its state, so a Released one would still read as spend. Keep it uncertain.
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

    /// <summary>Return an admission estimate whose call provably spent nothing (see <see cref="ObservedNoSpend"/>) — best-effort exactly like the settle below: a ledger fault must never replace the provider's own failure with an accounting one.</summary>
    private static async Task ReleaseQuietlyAsync(IBudgetLedger budget, LlmCallScope scope, string kind, string scopeKey, CancellationToken cancellationToken)
    {
        try { await budget.ReleaseAsync(scope.RunId, scope.TeamId, kind, scopeKey, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* torn down — the expiry sweep reconciles the live reservation */ }
        catch { /* best-effort — see summary */ }
    }

    /// <summary>Preserve the provider outcome if accounting persistence fails. Its existing reservation retains the admission estimate, not a guaranteed bound on the unknown bill; recovery and cost qualification must keep that distinction.</summary>
    private static async Task SettleQuietlyAsync(IBudgetLedger budget, LlmCallScope scope, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
    {
        try { await budget.SettleAsync(scope.RunId, scope.TeamId, kind, scopeKey, actualUsd, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* torn down — the expiry sweep reconciles the live reservation */ }
        catch { /* best-effort — see summary */ }
    }
}
