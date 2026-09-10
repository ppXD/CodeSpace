using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the W-hard atomic brain-plane guard — reserve-before-call, settle-at-actual, at the one funnel every
/// model call rides. Pins: a scope WITH a ledger but no cap value (an operator's deliberate no-cap-configured
/// launch) still RECORDS, as an "unbudgeted:" row naming the reason, instead of the row-less silent passthrough it
/// used to be; a scope with NO ledger at all — or no scope — is a P15-5a programming-error signal and THROWS
/// instead of the old silent fail-open, unless the caller marked itself explicitly Unbudgeted, which still passes
/// through but is recorded under an "unbudgeted:" ledger kind; a refused admission throws BEFORE the model is ever
/// invoked (the overshoot never happens); an admitted call settles at its actual spend; a failed call settles to
/// what was OBSERVED — headroom goes back ONLY when unbilled is PROVEN (an error STATUS, or a socket error saying
/// the request never reached a server), while everything ambiguous holds (a status-less timeout or reset, a
/// truncated body, a cancellation, a 2xx that may have billed, a failure that follows a billed attempt in the same
/// reservation); an unpriceable model UNDER A CAP is refused before the call (D1 fail-closed) while an uncapped one
/// passes through, and a failed-over successor is judged on its own price; the pessimistic estimate constants are
/// committed values.
///
/// <para>This pins how the guard READS a failure. That the re-ask paths actually MARK theirs
/// (<c>LlmApiException.PriorBilledAttempt</c>) is pinned over the real provider wire in
/// <see cref="StructuredResponseContractTests"/>.</para>
/// </summary>
[Trait("Category", "Unit")]
public class LlmBudgetGuardTests
{
    private static LlmCallScope Scope(IBudgetLedger? budget, decimal? cap) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "sup", "k", "supervisor.decision", null!, null!, budget, cap);

    [Fact]
    public async Task A_ledgered_scope_with_no_cap_value_records_an_unbudgeted_row_naming_the_reason()
    {
        // The fail-closed / fail-loud rules below are both scoped to "this plane never wired a ledger at all". An
        // operator who simply configured no cap on an otherwise-instrumented launch is not that, so it must never
        // start throwing OR blocking. What it must ALSO not be is what it used to be: `return await call(...)` —
        // the one silent, row-less passthrough left standing behind this class's "never a silent passthrough"
        // claim, which answered "what did this run spend on models" with an absence. It now takes the same
        // Unbudgeted path a plane with no launch takes, so the call is recorded by name.
        var ledger = new RecordingLedger(admit: true);

        (await LlmBudgetGuard.GuardedAsync(Scope(ledger, null), "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(42), _ => 0.1m, CancellationToken.None)).ShouldBe(42);

        ledger.Reserves.ShouldBe(1, "a budgeted scope with no cap VALUE is recorded, not silently waved through");
        ledger.LastReserveKind.ShouldBe($"{BudgetKinds.UnbudgetedPrefix}supervisor.decision");
        ledger.LastReserveCapUsd.ShouldBeNull("there is no cap to admit against — the row can never refuse and can never poison a real cap");
        ledger.LastSettleActual.ShouldBe(0.1m, "and it settles at the observed spend like any other row");
        LlmBudgetGuard.NoRunCapReason.ShouldBe("run has no MaxCostUsd", "the operator-facing phrase on the row is a committed value");
    }

    [Fact]
    public async Task A_null_scope_throws_UnscopedModelCall_instead_of_silently_passing_through()
    {
        // P15-5a: GuardedAsync is only ever called by the two recording decorators, and only AFTER they already
        // confirmed LlmCallContext.Current was non-null — so a null scope reaching here in production would mean
        // a THIRD, unguarded call site. This pins the guard's own defensive contract in isolation.
        var ex = await Should.ThrowAsync<UnscopedModelCallException>(() =>
            LlmBudgetGuard.GuardedAsync(scope: null, "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(42), _ => 0.1m, CancellationToken.None));

        ex.Plane.ShouldBe("(unscoped)");
        ex.CallSite.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public async Task A_scope_with_no_budget_ledger_throws_UnscopedModelCall_naming_the_plane()
    {
        // The exact defect this slice closes: a plane that pushed a real run/node correlation but never threaded
        // a budget ledger through used to spend past its launch's cap unmetered AND unlogged. Now it is a loud,
        // typed programming-error signal instead — regardless of whatever CapUsd happens to carry.
        var called = false;

        var ex = await Should.ThrowAsync<UnscopedModelCallException>(() =>
            LlmBudgetGuard.GuardedAsync(Scope(null, 5m), "claude-opus-4-8", "s", "u", 100, _ => { called = true; return Task.FromResult(42); }, _ => 0.1m, CancellationToken.None));

        called.ShouldBeFalse("the whole point: an unscoped call is refused before it can spend anything");
        ex.Plane.ShouldBe("supervisor.decision");
        ex.CallSite.ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public async Task An_unbudgeted_scope_passes_through_and_records_an_observability_row()
    {
        // A plane that legitimately has no launch (operator calibration, a plain workflow node, a benchmark
        // cell's own cap) marks itself Unbudgeted instead of leaving Budget unset — still never blocked, but no
        // longer a silent passthrough: the spend is recorded under an "unbudgeted:" kind for observability.
        var ledger = new RecordingLedger(admit: true);
        var scope = Scope(ledger, cap: null).Unbudgeted("no launch for this plane");

        (await LlmBudgetGuard.GuardedAsync(scope, "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(42), _ => 0.33m, CancellationToken.None)).ShouldBe(42);

        ledger.Reserves.ShouldBe(1, "Unbudgeted still records — the fix is visibility, not enforcement");
        ledger.LastReserveKind.ShouldStartWith(BudgetKinds.UnbudgetedPrefix);
        ledger.LastReserveCapUsd.ShouldBeNull("a null cap — never a poison sentinel a reader could mistake for a real committed cap");
        ledger.LastSettleActual.ShouldBe(0.33m);
    }

    [Fact]
    public void Marking_a_scope_Unbudgeted_clears_any_real_cap_it_was_carrying()
    {
        // A scope that ALSO carries a real Budget + CapUsd would otherwise take the structured decorator's
        // native-physical accounting path (gated on Budget-and-CapUsd both non-null), which enforces the cap for
        // real and never reads UnbudgetedReason at all — silently contradicting "Unbudgeted never throws". Clearing
        // CapUsd here makes that guarantee true by construction rather than by caller convention.
        var scope = Scope(new RecordingLedger(admit: true), cap: 5m).Unbudgeted("no launch for this plane");

        scope.CapUsd.ShouldBeNull();
    }

    [Fact]
    public async Task An_unbudgeted_scope_with_no_ledger_at_all_still_passes_through()
    {
        // Some Unbudgeted planes (e.g. a Hangfire-job executor with no IBudgetLedger reference reachable) have
        // nothing to record into. The call must still proceed — logging is the only thing it can guarantee.
        var scope = Scope(null, null).Unbudgeted("no ledger reachable from this executor");

        (await LlmBudgetGuard.GuardedAsync(scope, "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(7), _ => 0.1m, CancellationToken.None)).ShouldBe(7);
    }

    [Fact]
    public async Task An_unbudgeted_scope_never_refuses_an_unpriceable_model()
    {
        // There is no cap to protect here, so D1's fail-closed refusal (UnpricedModelUnderCapException) must
        // never fire for an Unbudgeted call — unlike the same model under a REAL cap (see the sibling test below).
        var ledger = new RecordingLedger(admit: true);
        var scope = Scope(ledger, cap: null).Unbudgeted("no launch for this plane");

        (await LlmBudgetGuard.GuardedAsync(scope, "totally-unknown-model", "s", "u", 100, _ => Task.FromResult(1), _ => null, CancellationToken.None)).ShouldBe(1);

        ledger.Reserves.ShouldBe(1, "still recorded, at the best-effort $0 estimate for an unpriced model");
    }

    [Fact]
    public async Task An_unbudgeted_faulted_call_settles_pessimistically_and_rethrows()
    {
        var ledger = new RecordingLedger(admit: true);
        var scope = Scope(ledger, cap: null).Unbudgeted("no launch for this plane");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            LlmBudgetGuard.GuardedAsync<int>(scope, "claude-opus-4-8", "s", "u", 100, _ => throw new InvalidOperationException("boom"), _ => 0m, CancellationToken.None));

        ledger.Settles.ShouldBe(1);
        ledger.LastSettleActual.ShouldBeNull("actual unknowable ⇒ the null-actual settle holds the observability reserve, same as the metered path");
    }

    [Fact]
    public async Task A_refused_admission_throws_before_the_model_is_ever_invoked()
    {
        var ledger = new RecordingLedger(admit: false);
        var invoked = false;

        var ex = await Should.ThrowAsync<LlmBudgetExceededException>(() =>
            LlmBudgetGuard.GuardedAsync(Scope(ledger, 5m), "claude-opus-4-8", "s", "u", 100, _ => { invoked = true; return Task.FromResult(0); }, _ => 0m, CancellationToken.None));

        invoked.ShouldBeFalse("the whole point: the spend that would overshoot NEVER happens");
        ex.CapUsd.ShouldBe(5m);
        ledger.Settles.ShouldBe(0, "nothing was spent, nothing settles");
    }

    [Fact]
    public async Task An_admitted_call_settles_at_its_actual_spend()
    {
        var ledger = new RecordingLedger(admit: true);

        var result = await LlmBudgetGuard.GuardedAsync(Scope(ledger, 5m), "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(7), _ => 0.25m, CancellationToken.None);

        result.ShouldBe(7);
        ledger.Reserves.ShouldBe(1);
        ledger.LastSettleActual.ShouldBe(0.25m, "the settle corrects the pessimistic reserve to the observed spend");
    }

    [Fact]
    public async Task A_faulted_call_settles_pessimistically_and_rethrows()
    {
        var ledger = new RecordingLedger(admit: true);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            LlmBudgetGuard.GuardedAsync<int>(Scope(ledger, 5m), "claude-opus-4-8", "s", "u", 100, _ => throw new InvalidOperationException("boom"), _ => 0m, CancellationToken.None));

        ledger.Settles.ShouldBe(1);
        ledger.LastSettleActual.ShouldBeNull("actual unknowable ⇒ the ledger's null-actual settle holds the reserve — the only safe direction");
    }

    public static TheoryData<Exception, bool, string> FailureShapes() => new()
    {
        { new LlmApiException("Anthropic", 429, LlmErrorCategory.RateLimited, "rate limited"), true, "a 429 is a RESPONSE, and an error response carries no completion — holding its estimate is how a retry storm fills the cap with phantoms" },
        { new LlmApiException("Anthropic", 503, LlmErrorCategory.Transient, "unavailable"), true, "a 5xx status is the gateway answering that it produced nothing" },
        { new LlmApiException("Anthropic", 502, LlmErrorCategory.Transient, "bad gateway"), true, "the same proof at the other end of the 5xx range — the STATUS is what proves it, not the category" },
        { new LlmApiException("OpenAI", 401, LlmErrorCategory.AuthFailed, "bad key"), true, "a rejected key cannot have been billed" },
        { new LlmApiException("OpenAI", 400, LlmErrorCategory.BadRequest, "unsupported tool_choice"), true, "the gateway refused the request shape — the format fault that failover retries per hop" },
        { new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused)), true, "nothing was ever sent, so nothing could have been generated" },
        { new HttpRequestException("no such host", new SocketException((int)SocketError.HostNotFound)), true, "a DNS miss never reached a server" },
        { new LlmApiException("OpenAI", 400, LlmErrorCategory.ContextLengthExceeded, "too long"), true, "a 400 is a refusal to generate, whatever the body keywords refined the category to" },
        { new LlmApiException("Anthropic", 400, LlmErrorCategory.ContentFiltered, "blocked"), true, "a screened-out request the gateway answered with a 400 returned no completion; a filter applied to a 2xx REPLY arrives as a status-less fault and holds below" },
        { new LlmApiException("Anthropic", null, LlmErrorCategory.Transient, "the request timed out before the gateway responded"), false, "THE defect: a client-side timeout is a status-LESS Transient — we stopped listening, and the provider may have generated and billed the completion anyway" },
        { new LlmApiException("Anthropic", null, LlmErrorCategory.Transient, "connection reset", inner: new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset))), false, "a reset AFTER the request was sent is not proof of anything — only a refused/unroutable/unresolved connection is" },
        { new HttpRequestException("error while copying content to a stream"), false, "a truncated read of a 200 body — the completion was generated and billed, we just did not finish reading it" },
        { new LlmApiException("OpenAI", 200, LlmErrorCategory.Malformed, "not json"), false, "a 2xx MAY have billed — its own doc says so; never release on an ambiguous outcome" },
        { new LlmApiException("OpenAI", 429, LlmErrorCategory.RateLimited, "rate limited") { PriorBilledAttempt = true }, false, "a re-ask's 429 proves nothing about the first physical call, which was billed inside this same reservation" },
        { new OperationCanceledException("cancelled", new LlmApiException("Anthropic", 502, LlmErrorCategory.Transient, "bad gateway")), false, "the OUTER cancellation is a veto — it wins over the INNER 502's own proof; a caller that wrapped its own cancellation around a released-worthy fault must not launder it into a release" },
        { new TaskCanceledException("HttpClient timeout"), false, "the request may well have been served and billed after we stopped listening" },
        { new TimeoutException("elapsed"), false, "same ambiguity as a cancellation" },
        { new InvalidOperationException("boom"), false, "an untyped fault proves nothing — the pre-existing pessimistic hold" },
    };

    [Theory]
    [MemberData(nameof(FailureShapes))]
    public async Task A_failed_call_settles_to_what_was_actually_observed(Exception thrown, bool releasesHeadroom, string why)
    {
        // THE defect: every failure took the null-actual settle, which lands Indeterminate and HOLDS the estimate
        // forever (no fact source ever settles an llm: row, and the reconcile pass keeps holding it). A 429 /
        // format-fault storm across failover hops — each hop its OWN reservation — therefore filled the run's cap
        // with phantom estimates for calls that produced no tokens, and the next REAL call was refused. So the
        // catch has to distinguish "provably spent nothing" from "might have been billed".
        //
        // The proof is the STATUS (an error response carries no completion) or the SOCKET ERROR (the request never
        // reached a server) — never the retry CATEGORY, which collapses a 503 together with a client-side timeout
        // and a mid-flight reset, both of which reached a provider that bills what it generated.
        var ledger = new RecordingLedger(admit: true);

        await Should.ThrowAsync<Exception>(() =>
            LlmBudgetGuard.GuardedAsync<int>(Scope(ledger, 5m), "claude-opus-4-8", "s", "u", 100, _ => throw thrown, _ => 0m, CancellationToken.None));

        ledger.Reserves.ShouldBe(1, "the fixture must actually have reserved, or this measures nothing");
        ledger.Releases.ShouldBe(releasesHeadroom ? 1 : 0, why);
        ledger.Settles.ShouldBe(releasesHeadroom ? 0 : 1, why);
        if (!releasesHeadroom) ledger.LastSettleActual.ShouldBeNull("an ambiguous outcome keeps the reserve without inventing a bill");
    }

    [Fact]
    public async Task A_released_transport_failure_leaves_the_headroom_for_the_next_call()
    {
        // The consequence the Theory above only implies: two hops that both 429'd must not have consumed the cap.
        var ledger = new RecordingLedger(admit: true);

        for (var hop = 0; hop < 2; hop++)
            await Should.ThrowAsync<LlmApiException>(() => LlmBudgetGuard.GuardedAsync<int>(Scope(ledger, 5m), "claude-opus-4-8", "s", "u", 100,
                _ => throw new LlmApiException("Anthropic", 429, LlmErrorCategory.RateLimited, "rate limited"), _ => 0m, CancellationToken.None));

        ledger.Releases.ShouldBe(2, "each hop reserves and releases its own claim — a failover chain cannot spend a cap on calls that produced nothing");
        ledger.Settles.ShouldBe(0);
    }

    [Fact]
    public void An_ambiguous_outcome_wrapped_in_a_transport_shape_is_still_ambiguous()
    {
        // The chain walk looks for PROOF anywhere in it, and the two vetoes outrank any proof below them: a caller
        // that wrapped its own cancellation around a transport fault must not launder it into a release, and
        // neither may a re-ask's failure that sits above a first call this reservation already paid for.
        var refused = new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused));

        LlmBudgetGuard.ObservedNoSpend(new TaskCanceledException("cancelled", refused)).ShouldBeFalse("a cancellation above the proof wins");
        LlmBudgetGuard.ObservedNoSpend(new InvalidOperationException("wrapper", refused)).ShouldBeTrue("an untyped wrapper around a proven-unbilled transport fault is still that fault");
        LlmBudgetGuard.ObservedNoSpend(new InvalidOperationException("wrapper", new HttpRequestException("reset"))).ShouldBeFalse("a transport fault with no socket error proves nothing — the reset may have followed a billed request");
        LlmBudgetGuard.ObservedNoSpend(new LlmApiException("OpenAI", null, LlmErrorCategory.Transient, "reset", inner: refused) { PriorBilledAttempt = true }).ShouldBeFalse("a prior BILLED attempt vetoes even a proven-unbilled second one — one reservation covers both");
    }

    [Fact]
    public async Task An_unpriceable_model_UNDER_A_CAP_is_refused_before_the_model_is_ever_called()
    {
        // D1 — the behaviour this test used to pin (fail-OPEN: pass the call through) is exactly the defect: the
        // spend then folds back as $0, the cap never trips, and the run bills unbounded while terminalizing
        // Success. Under a cap an unpriceable model is a cap that cannot be enforced, so it is refused.
        var ledger = new RecordingLedger(admit: true);
        var called = false;

        var refusal = await Should.ThrowAsync<UnpricedModelUnderCapException>(() =>
            LlmBudgetGuard.GuardedAsync(Scope(ledger, 5m), "totally-unknown-model", "s", "u", 100, _ => { called = true; return Task.FromResult(1); }, _ => 0m, CancellationToken.None));

        called.ShouldBeFalse("the money is never spent — the refusal precedes the call");
        ledger.Reserves.ShouldBe(0, "nothing is reserved either; there is no price to reserve against");
        refusal.Model.ShouldBe("totally-unknown-model");
        refusal.Detail.ShouldContain("totally-unknown-model");
        refusal.Detail.ShouldContain("model manager", Case.Insensitive, "the refusal must name the remedy, not just the problem");
    }

    [Fact]
    public async Task An_unpriceable_model_with_NO_cap_still_passes_through_untouched()
    {
        // The fail-closed rule is scoped to a declared cap. An uncapped run keeps D1's uncapped behaviour — an
        // unknown cost stays unknown and NOTHING blocks, even though the call is now recorded: the observability
        // row is reserved with a null cap, so this refusing ledger can never actually refuse it.
        var ledger = new RecordingLedger(admit: false);   // would refuse if it were ever an admission gate

        (await LlmBudgetGuard.GuardedAsync(Scope(ledger, cap: null), "totally-unknown-model", "s", "u", 100, _ => Task.FromResult(1), _ => 0m, CancellationToken.None)).ShouldBe(1);

        ledger.LastReserveKind.ShouldStartWith(BudgetKinds.UnbudgetedPrefix, Case.Sensitive, "an unpriceable model under no cap is recorded at its best-effort $0 estimate, never refused");
    }

    [Fact]
    public async Task A_model_priced_only_by_the_operators_OWN_row_is_admitted_under_a_cap()
    {
        // The whole point of the per-row price: a Codex/OpenAI/Custom pool id the built-in table never heard of
        // becomes spendable under a cap the moment the operator prices it — no code change, no env var.
        var ledger = new RecordingLedger(admit: true);
        var prices = new Dictionary<string, CodeSpace.Messages.Agents.ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.4-codex"] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 10m },
        };

        var scope = Scope(ledger, 5m) with { ModelPrices = prices };

        (await LlmBudgetGuard.GuardedAsync(scope, "gpt-5.4-codex", "s", "u", 100, _ => Task.FromResult(1), _ => 0.01m, CancellationToken.None)).ShouldBe(1);

        ledger.Reserves.ShouldBe(1, "priced ⇒ estimable ⇒ reserved, exactly like a built-in model");
    }

    [Fact]
    public async Task A_FAILED_OVER_successor_model_is_judged_on_ITS_OWN_price_not_the_first_picks()
    {
        // The brain pool fails a call over to another row (#1737/#1738); each attempt re-enters this guard under
        // its own model name. So a PRICED first pick can never launder an UNPRICED successor past the cap, and an
        // unpriced first pick can never poison a priced successor.
        var ledger = new RecordingLedger(admit: true);
        var prices = new Dictionary<string, CodeSpace.Messages.Agents.ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["priced-primary"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m },
        };
        var scope = Scope(ledger, 5m) with { ModelPrices = prices };

        // Attempt 1: the priced primary is admitted.
        (await LlmBudgetGuard.GuardedAsync(scope, "priced-primary", "s", "u", 100, _ => Task.FromResult(1), _ => 0.01m, CancellationToken.None)).ShouldBe(1);

        // Attempt 2 (the failover): the successor carries NO price → refused on its own merits.
        var refusal = await Should.ThrowAsync<UnpricedModelUnderCapException>(() =>
            LlmBudgetGuard.GuardedAsync(scope, "unpriced-successor", "s", "u", 100, _ => Task.FromResult(1), _ => 0.01m, CancellationToken.None));

        refusal.Model.ShouldBe("unpriced-successor", "the stop names the model that actually could not be priced");
    }

    [Fact]
    public async Task Every_reservation_carries_the_committed_ttl_so_an_orphan_can_never_sit_live_forever()
    {
        // W-hard slice 2: a teardown between reserve and settle leaves an orphan — without a deadline it holds
        // headroom invisibly forever (the expiry sweep only targets rows WITH one), taxing every later call of a
        // reclaimed run. The TTL is generously past any real call; only true orphans ever reach it.
        var ledger = new RecordingLedger(admit: true);

        await LlmBudgetGuard.GuardedAsync(Scope(ledger, 5m), "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(1), _ => 0.1m, CancellationToken.None);

        LlmBudgetGuard.ReservationTtl.ShouldBe(TimeSpan.FromMinutes(30));
        ledger.LastExpiresAt.ShouldNotBeNull("a deadline-less reservation is invisible to the expiry sweep");
        ledger.LastExpiresAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(25));
        ledger.LastExpiresAt!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(35));
    }

    [Fact]
    public void Admission_estimates_are_available_for_priced_models()
    {
        LlmBudgetGuard.DefaultMaxOutputTokensEstimate.ShouldBe(8192);
        LlmBudgetGuard.EstimateUsd("claude-opus-4-8", new string('x', 3000), new string('y', 3000), maxOutputTokens: null)
            .ShouldNotBeNull("a priced model always estimates");
        LlmBudgetGuard.EstimateUsd("totally-unknown-model", "s", "u", 100).ShouldBeNull();
    }

    [Fact]
    public async Task An_unrepresentable_positive_admission_estimate_cannot_create_a_free_claim()
    {
        var ledger = new RecordingLedger(admit: true);
        var scope = Scope(ledger, 1m) with { ModelPrices = new Dictionary<string, CodeSpace.Messages.Agents.ModelPrice>
        {
            ["tiny"] = new() { InputPerMillionUsd = 0.0000000000000000000000000001m, OutputPerMillionUsd = 0m },
        } };
        var called = false;
        await Should.ThrowAsync<UnpricedModelUnderCapException>(() => LlmBudgetGuard.GuardedAsync(scope, "tiny", "s", "u", 1, _ => { called = true; return Task.FromResult(1); }, _ => null, CancellationToken.None));
        called.ShouldBeFalse();
        ledger.Reserves.ShouldBe(0);
    }

    [Fact]
    public async Task A_replayed_logical_claim_never_authorizes_another_provider_request()
    {
        var ledger = new RecordingLedger(admit: true, replay: true);
        var called = false;
        var refusal = await Should.ThrowAsync<LlmBudgetExceededException>(() => LlmBudgetGuard.GuardedAsync(Scope(ledger, 1m), "claude-opus-4-8", "s", "u", 10, _ => { called = true; return Task.FromResult(1); }, _ => 0m, CancellationToken.None));
        called.ShouldBeFalse();
        ledger.Settles.ShouldBe(0);
        refusal.Message.ShouldContain("cannot authorize another provider request");
    }

    private sealed class RecordingLedger(bool admit, bool replay = false) : IBudgetLedger
    {
        public int Reserves;
        public int Settles;
        public decimal? LastSettleActual;
        public string? LastReserveKind;
        public decimal? LastReserveCapUsd;

        public DateTimeOffset? LastExpiresAt;

        public Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal? capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
        {
            Reserves++;
            LastExpiresAt = expiresAt;
            LastReserveKind = kind;
            LastReserveCapUsd = capUsd;
            return Task.FromResult(new BudgetAdmission(admit, admit ? Guid.NewGuid() : null, 4.9m, capUsd, admit ? null : "cap") { IsReplay = replay });
        }

        public Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
        {
            Settles++;
            LastSettleActual = actualUsd;
            return Task.CompletedTask;
        }

        public int Releases;

        public Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken)
        {
            Releases++;
            return Task.CompletedTask;
        }

        public Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(0m);
        public Task<decimal> CommittedTeamUsdAsync(Guid teamId, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

    }
}
