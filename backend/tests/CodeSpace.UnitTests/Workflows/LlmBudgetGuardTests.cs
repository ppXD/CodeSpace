using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the W-hard atomic brain-plane guard — reserve-before-call, settle-at-actual, at the one funnel every
/// model call rides. Pins: a scope without a ledger+cap (every pre-slice pusher) passes through untouched; a
/// refused admission throws BEFORE the model is ever invoked (the overshoot never happens); an admitted call
/// settles at its actual spend; a faulted call settles pessimistically (null actual = at the reserve); an
/// unpriceable model UNDER A CAP is refused before the call (D1 fail-closed) while an uncapped one passes through,
/// and a failed-over successor is judged on its own price; the pessimistic estimate constants are committed values.
/// </summary>
[Trait("Category", "Unit")]
public class LlmBudgetGuardTests
{
    private static LlmCallScope Scope(IBudgetLedger? budget, decimal? cap) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "sup", "k", "supervisor.decision", null!, null!, budget, cap);

    [Fact]
    public async Task A_scope_without_a_ledger_or_cap_passes_through_untouched()
    {
        var ledger = new RecordingLedger(admit: true);

        (await LlmBudgetGuard.GuardedAsync(Scope(null, 5m), "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(42), _ => 0.1m, CancellationToken.None)).ShouldBe(42);
        (await LlmBudgetGuard.GuardedAsync(Scope(ledger, null), "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(42), _ => 0.1m, CancellationToken.None)).ShouldBe(42);
        (await LlmBudgetGuard.GuardedAsync(scope: null, "claude-opus-4-8", "s", "u", 100, _ => Task.FromResult(42), _ => 0.1m, CancellationToken.None)).ShouldBe(42);

        ledger.Reserves.ShouldBe(0, "no cap ⇒ no reservation — byte-identical for every pre-slice pusher");
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
        // The fail-closed rule is scoped to a declared cap. An uncapped run is byte-identical to before D1 — an
        // unknown cost stays unknown and nothing blocks.
        var ledger = new RecordingLedger(admit: false);   // would refuse if consulted

        (await LlmBudgetGuard.GuardedAsync(Scope(ledger, cap: null), "totally-unknown-model", "s", "u", 100, _ => Task.FromResult(1), _ => 0m, CancellationToken.None)).ShouldBe(1);

        ledger.Reserves.ShouldBe(0);
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
    public void The_pessimistic_estimate_is_committed_and_directionally_safe()
    {
        LlmBudgetGuard.DefaultMaxOutputTokensEstimate.ShouldBe(8192);
        LlmBudgetGuard.EstimateUsd("claude-opus-4-8", new string('x', 3000), new string('y', 3000), maxOutputTokens: null)
            .ShouldNotBeNull("a priced model always estimates");
        LlmBudgetGuard.EstimateUsd("totally-unknown-model", "s", "u", 100).ShouldBeNull();
    }

    private sealed class RecordingLedger(bool admit) : IBudgetLedger
    {
        public int Reserves;
        public int Settles;
        public decimal? LastSettleActual;

        public DateTimeOffset? LastExpiresAt;

        public Task<BudgetAdmission> ReserveAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal estimateUsd, decimal capUsd, string priceVersion, Guid? parentReservationId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
        {
            Reserves++;
            LastExpiresAt = expiresAt;
            return Task.FromResult(new BudgetAdmission(admit, admit ? Guid.NewGuid() : null, 4.9m, capUsd, admit ? null : "cap"));
        }

        public Task SettleAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, decimal? actualUsd, CancellationToken cancellationToken)
        {
            Settles++;
            LastSettleActual = actualUsd;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(Guid workflowRunId, Guid teamId, string kind, string scopeKey, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> ExpireOverdueAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<decimal> CommittedUsdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => Task.FromResult(0m);
    public Task<int> ReconcileDanglingAsync(string kindPrefix, int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

    }
}
