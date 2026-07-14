using Autofac;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): the W-hard budget ledger's laws — atomic admission under THE invariant
/// (settled + live ≤ cap) serialized by the per-run advisory lock (two concurrent waves can never jointly
/// overshoot), idempotent reservation, pessimistic settlement (unknown actual settles AT reserved, never lower),
/// release returns headroom, and expiry goes Indeterminate HOLDING its claim (never a silent free).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BudgetLedgerFlowTests
{
    private readonly PostgresFixture _fixture;

    public BudgetLedgerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Admission_enforces_the_cap_and_replays_are_idempotent()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        var first = await ledger.ReserveAsync(runId, teamId, "agent-attempt", "s1#a1", estimateUsd: 4m, capUsd: 10m, "prices-v1", null, null, CancellationToken.None);
        first.Admitted.ShouldBeTrue();

        var replay = await ledger.ReserveAsync(runId, teamId, "agent-attempt", "s1#a1", estimateUsd: 4m, capUsd: 10m, "prices-v1", null, null, CancellationToken.None);
        replay.Admitted.ShouldBeTrue();
        replay.ReservationId.ShouldBe(first.ReservationId, "a crash-replayed producer lands on its own row");

        (await ledger.ReserveAsync(runId, teamId, "agent-attempt", "s2#a1", 4m, 10m, "prices-v1", null, null, CancellationToken.None)).Admitted.ShouldBeTrue();

        var overCap = await ledger.ReserveAsync(runId, teamId, "agent-attempt", "s3#a1", 4m, 10m, "prices-v1", null, null, CancellationToken.None);
        overCap.Admitted.ShouldBeFalse("4+4+4 would commit 12 past the 10 cap — THE invariant");
        overCap.Reason.ShouldNotBeNull();
    }

    [Fact]
    public async Task Concurrent_waves_can_never_jointly_overshoot()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        // 8 parallel reserves of 3 against a 10 cap — at most 3 may admit (9 ≤ 10 < 12), regardless of interleaving.
        var admissions = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IBudgetLedger>().ReserveAsync(runId, teamId, "agent-attempt", $"s{i}", 3m, 10m, "prices-v1", null, null, CancellationToken.None);
        }));

        admissions.Count(a => a.Admitted).ShouldBe(3, "the advisory lock serializes admission — mid-wave overshoot is structurally impossible");
    }

    [Fact]
    public async Task Settlement_is_pessimistic_and_release_returns_headroom()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        await ledger.ReserveAsync(runId, teamId, "agent-attempt", "actual", 5m, 100m, "prices-v1", null, null, CancellationToken.None);
        await ledger.SettleAsync(runId, teamId, "agent-attempt", "actual", actualUsd: 2.5m, CancellationToken.None);

        await ledger.ReserveAsync(runId, teamId, "agent-attempt", "unknown", 5m, 100m, "prices-v1", null, null, CancellationToken.None);
        await ledger.SettleAsync(runId, teamId, "agent-attempt", "unknown", actualUsd: null, CancellationToken.None);

        await ledger.ReserveAsync(runId, teamId, "agent-attempt", "never-ran", 5m, 100m, "prices-v1", null, null, CancellationToken.None);
        await ledger.ReleaseAsync(runId, teamId, "agent-attempt", "never-ran", CancellationToken.None);

        // 2.5 (actual) + 5 (pessimistic) + 0 (released) — the unknown actual held its full claim.
        (await ledger.CommittedUsdAsync(runId, teamId, CancellationToken.None)).ShouldBe(7.5m);
    }

    [Fact]
    public async Task Expiry_holds_the_claim_instead_of_silently_freeing_it()
    {
        var (teamId, _) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var ledger = scope.Resolve<IBudgetLedger>();

        await ledger.ReserveAsync(runId, teamId, "agent-attempt", "orphan", 5m, 100m, "prices-v1", null, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        (await ledger.ExpireOverdueAsync(batchSize: 10, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        (await ledger.CommittedUsdAsync(runId, teamId, CancellationToken.None)).ShouldBe(5m,
            "an overdue reservation goes Indeterminate and HOLDS its headroom until a reconcile pass decides — expiry never silently frees the cap");
    }
}
