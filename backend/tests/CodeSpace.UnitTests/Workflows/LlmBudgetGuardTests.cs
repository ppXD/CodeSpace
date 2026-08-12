using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the W-hard atomic brain-plane guard — reserve-before-call, settle-at-actual, at the one funnel every
/// model call rides. Pins: a scope without a ledger+cap (every pre-slice pusher) passes through untouched; a
/// refused admission throws BEFORE the model is ever invoked (the overshoot never happens); an admitted call
/// settles at its actual spend; a faulted call settles pessimistically (null actual = at the reserve); an
/// unpriceable model fails open like the cost plane; the pessimistic estimate constants are committed values.
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
    public async Task An_unpriceable_model_fails_open_like_the_cost_plane()
    {
        var ledger = new RecordingLedger(admit: false);   // would refuse if consulted

        (await LlmBudgetGuard.GuardedAsync(Scope(ledger, 5m), "totally-unknown-model", "s", "u", 100, _ => Task.FromResult(1), _ => 0m, CancellationToken.None)).ShouldBe(1);

        ledger.Reserves.ShouldBe(0);
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
