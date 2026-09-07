using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BudgetAccountingFlowTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Unknown_cost_holds_the_claim_without_inventing_an_actual_and_late_usage_can_settle_it()
    {
        var scenario = await SeedAsync();
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        await ReserveAsync(ledger, scenario);
        await ledger.SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", null, CancellationToken.None);

        var unknown = await RowAsync(scenario);
        unknown.State.ShouldBe(BudgetReservationStates.Indeterminate);
        unknown.SettledUsd.ShouldBeNull("a reservation estimate is not an observed bill");
        (await ledger.CommittedUsdAsync(scenario.RunId, scenario.TeamId, CancellationToken.None)).ShouldBe(5m);
        (await ledger.ReserveAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "next", 1m, 5m, "test-v1", null, null, CancellationToken.None)).Admitted.ShouldBeFalse();

        await ledger.ReconcileDanglingAsync("llm:", 100, CancellationToken.None);
        (await RowAsync(scenario)).SettledUsd.ShouldBeNull("terminal bookkeeping must retain cost uncertainty");
        await ledger.SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", 2m, CancellationToken.None);
        var settled = await RowAsync(scenario);
        settled.State.ShouldBe(BudgetReservationStates.Settled);
        settled.SettledUsd.ShouldBe(2m, "a late provider receipt supersedes an unresolved estimate");
    }

    [Fact]
    public async Task A_stale_reader_cannot_release_another_workers_actual_settlement()
    {
        var scenario = await SeedAsync();
        using var stale = fixture.BeginScope();
        await ReserveAsync(stale.Resolve<IBudgetLedger>(), scenario);
        // The scope that made the reservation still tracks Reserved after a different worker settles it.
        using (var writer = fixture.BeginScope())
            await writer.Resolve<IBudgetLedger>().SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", 2m, CancellationToken.None);

        await stale.Resolve<IBudgetLedger>().ReleaseAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", CancellationToken.None);
        var row = await RowAsync(scenario);
        row.State.ShouldBe(BudgetReservationStates.Settled);
        row.SettledUsd.ShouldBe(2m);
        (await stale.Resolve<IBudgetLedger>().CommittedUsdAsync(scenario.RunId, scenario.TeamId, CancellationToken.None)).ShouldBe(2m);
    }

    [Fact]
    public async Task A_late_actual_receipt_survives_concurrent_unknown_settle_release_expiry_and_reconciliation()
    {
        var scenario = await SeedAsync();
        using (var seed = fixture.BeginScope()) await ReserveAsync(seed.Resolve<IBudgetLedger>(), scenario);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, 16).Select(async i =>
        {
            using var scope = fixture.BeginScope();
            var ledger = scope.Resolve<IBudgetLedger>();
            await start.Task;
            switch (i % 4)
            {
                case 0: await ledger.SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", i == 0 ? 2m : null, CancellationToken.None); break;
                case 1: await ledger.ReleaseAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", CancellationToken.None); break;
                case 2: await ledger.ExpireOverdueAsync(100, CancellationToken.None); break;
                default: await ledger.ReconcileDanglingAsync("llm:", 100, CancellationToken.None); break;
            }
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20));

        var row = await RowAsync(scenario);
        row.State.ShouldBe(BudgetReservationStates.Settled);
        row.SettledUsd.ShouldBe(2m, "a provider receipt cannot lose to stale or less certain bookkeeping");
    }

    [Fact]
    public async Task Tiny_positive_reservations_and_actuals_round_trip_without_becoming_free()
    {
        var scenario = await SeedAsync();
        const decimal amount = 0.0000000000000000000000000001m;
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        (await ledger.ReserveAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", amount, amount, "test-v1", null, null, CancellationToken.None)).Admitted.ShouldBeTrue();
        (await RowAsync(scenario)).ReservedUsd.ShouldBe(amount);
        (await ledger.ReserveAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "second", amount, amount, "test-v1", null, null, CancellationToken.None)).Admitted.ShouldBeFalse();
        await ledger.SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", amount, CancellationToken.None);
        (await RowAsync(scenario)).SettledUsd.ShouldBe(amount);
    }

    [Fact]
    public async Task A_lost_reservation_commit_ack_replays_one_claim_and_a_lost_settlement_ack_keeps_the_actual()
    {
        var scenario = await SeedAsync();
        var lostReserve = new LoseCommitReceipt();
        using (var scope = InterceptedScope(lostReserve))
            await Should.ThrowAsync<IOException>(() => ReserveAsync(scope.Resolve<IBudgetLedger>(), scenario));
        lostReserve.Lost.ShouldBe(1);

        using (var retry = fixture.BeginScope())
        {
            await ReserveAsync(retry.Resolve<IBudgetLedger>(), scenario);
            (await retry.Resolve<CodeSpaceDbContext>().BudgetReservation.CountAsync(r => r.WorkflowRunId == scenario.RunId)).ShouldBe(1);
        }
        var lostSettle = new LoseCommitReceipt();
        using (var scope = InterceptedScope(lostSettle))
            await Should.ThrowAsync<IOException>(() => scope.Resolve<IBudgetLedger>().SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", 2m, CancellationToken.None));
        lostSettle.Lost.ShouldBe(1, "settlement must commit independently of receiving its ACK");

        using var verify = fixture.BeginScope();
        await verify.Resolve<IBudgetLedger>().SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", null, CancellationToken.None);
        (await RowAsync(scenario)).SettledUsd.ShouldBe(2m);
        (await verify.Resolve<IBudgetLedger>().CommittedUsdAsync(scenario.RunId, scenario.TeamId, CancellationToken.None)).ShouldBe(2m);
    }

    [Fact]
    public async Task An_idempotency_key_cannot_be_replayed_from_a_different_team()
    {
        var scenario = await SeedAsync();
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        await ReserveAsync(ledger, scenario);
        var admission = await ledger.ReserveAsync(scenario.RunId, otherTeamId, "llm:critic.review", "call", 5m, 5m, "test-v1", null, null, CancellationToken.None);
        admission.Admitted.ShouldBeFalse("an existing claim is not authority to bill a different team");
        (await RowAsync(scenario)).TeamId.ShouldBe(scenario.TeamId);
    }

    [Fact]
    public async Task A_provider_failure_after_an_unused_release_reinstates_the_uncertain_claim()
    {
        var scenario = await SeedAsync();
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        await ReserveAsync(ledger, scenario);
        await ledger.ReleaseAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", CancellationToken.None);
        await ledger.SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", null, CancellationToken.None);
        var row = await RowAsync(scenario);
        row.State.ShouldBe(BudgetReservationStates.Indeterminate);
        row.SettledUsd.ShouldBeNull();
        (await ledger.CommittedUsdAsync(scenario.RunId, scenario.TeamId, CancellationToken.None)).ShouldBe(5m);
    }

    [Theory]
    [InlineData("estimate")]
    [InlineData("cap")]
    [InlineData("price")]
    [InlineData("parent")]
    [InlineData("expiry")]
    public async Task The_same_key_cannot_admit_a_different_reservation_intent(string changed)
    {
        var scenario = await SeedAsync();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        var first = await ledger.ReserveAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", 5m, 10m, "test-v1", null, expiry, CancellationToken.None);
        first.Admitted.ShouldBeTrue();
        var retry = await ledger.ReserveAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", 5m, 10m, "test-v1", null, expiry, CancellationToken.None);
        retry.Admitted.ShouldBeTrue("an identical request survives the database's timestamp precision");
        retry.ReservationId.ShouldBe(first.ReservationId);
        var changedIntent = await ledger.ReserveAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", changed == "estimate" ? 1m : 5m, changed == "cap" ? 20m : 10m, changed == "price" ? "test-v2" : "test-v1", changed == "parent" ? Guid.NewGuid() : null, changed == "expiry" ? expiry.AddMinutes(1) : expiry, CancellationToken.None);
        changedIntent.Admitted.ShouldBeFalse();
        changedIntent.Reason.ShouldBe("reservation-intent-mismatch");
        (await RowAsync(scenario)).ReservedUsd.ShouldBe(5m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_identical_terminal_claim_replay_returns_its_existing_state_without_a_new_claim(bool release)
    {
        var scenario = await SeedAsync();
        using var scope = fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();
        var first = await ReserveAsync(ledger, scenario);
        first.IsReplay.ShouldBeFalse();
        if (release) await ledger.ReleaseAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", CancellationToken.None);
        else await ledger.SettleAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", 2m, CancellationToken.None);
        var replay = await ReserveAsync(ledger, scenario);
        replay.Admitted.ShouldBeTrue("same-intent lookup is idempotent; IsReplay distinguishes it from permission for a new request");
        replay.IsReplay.ShouldBeTrue();
        replay.ReservationState.ShouldBe(release ? BudgetReservationStates.Released : BudgetReservationStates.Settled);
        replay.ReservationId.ShouldBe(first.ReservationId);
        replay.CommittedUsd.ShouldBe(release ? 0m : 2m);
    }

    [Fact]
    public async Task A_legacy_claim_with_no_frozen_cap_cannot_prove_an_identical_admission_intent()
    {
        var scenario = await SeedAsync();
        using var scope = fixture.BeginScope();
        await ReserveAsync(scope.Resolve<IBudgetLedger>(), scenario);
        await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.Where(r => r.WorkflowRunId == scenario.RunId).ExecuteUpdateAsync(setters => setters.SetProperty(r => r.CapUsd, (decimal?)null));
        var replay = await ReserveAsync(scope.Resolve<IBudgetLedger>(), scenario);
        replay.Admitted.ShouldBeFalse();
        replay.IsReplay.ShouldBeTrue();
        replay.ReservationState.ShouldBe(BudgetReservationStates.Reserved, "legacy state remains inspectable without inventing the historical cap");
    }

    private async Task<Scenario> SeedAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        return new Scenario(Guid.NewGuid(), teamId, DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    private static Task<BudgetAdmission> ReserveAsync(IBudgetLedger ledger, Scenario scenario) => ledger.ReserveAsync(scenario.RunId, scenario.TeamId, "llm:critic.review", "call", 5m, 5m, "test-v1", null, scenario.ExpiresAt, CancellationToken.None);

    private async Task<BudgetReservation> RowAsync(Scenario scenario)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().SingleAsync(r => r.WorkflowRunId == scenario.RunId && r.ScopeKey == "call");
    }

    private ILifetimeScope InterceptedScope(IInterceptor interceptor) => fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private sealed class LoseCommitReceipt : DbTransactionInterceptor
    {
        public int Lost { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Lost++;
            throw new IOException("budget commit acknowledgement lost");
        }
    }

    private sealed record Scenario(Guid RunId, Guid TeamId, DateTimeOffset ExpiresAt);
}
