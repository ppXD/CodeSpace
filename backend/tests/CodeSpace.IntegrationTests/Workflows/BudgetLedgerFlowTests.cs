using Microsoft.EntityFrameworkCore;
using Autofac;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages;
using Messages = CodeSpace.Messages;
using Infrastructure = CodeSpace.IntegrationTests.Workflows.Infrastructure;
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
    public async Task The_settlement_sweep_settles_folded_attempts_and_releases_terminal_orphans()
    {
        var (teamId, userId) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await Infrastructure.WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        var ledger = scope.Resolve<IBudgetLedger>();

        // Wave of 2 reserved at turn 1 (admission's key arithmetic), plus a third slice whose attempt never ran.
        await ledger.ReserveAsync(runId, teamId, "agent-attempt", "sup#turn1#0", 2m, 100m, "realized-v1", null, null, CancellationToken.None);
        await ledger.ReserveAsync(runId, teamId, "agent-attempt", "sup#turn1#1", 2m, 100m, "realized-v1", null, null, CancellationToken.None);
        await ledger.ReserveAsync(runId, teamId, "agent-attempt", "sup#turn2#0", 2m, 100m, "realized-v1", null, null, CancellationToken.None);

        // The tape: decision 0 = plan, decision 1 = the terminal spawn with 2 folded results (unknown model →
        // unpriceable → pessimistic settle at reserved). Turn number = ledger position.
        await SeedDecisionAsync(db, runId, teamId, 0, Messages.Agents.SupervisorDecisionKinds.Plan, "{}", "{}");
        await SeedDecisionAsync(db, runId, teamId, 1, Messages.Agents.SupervisorDecisionKinds.Spawn, """{"subtaskIds":["s1","s2"]}""",
            System.Text.Json.JsonSerializer.Serialize(new { agentResults = new[]
            {
                new { agentRunId = Guid.NewGuid(), status = "Succeeded" },
                new { agentRunId = Guid.NewGuid(), status = "Failed" },
            } }, CodeSpace.Core.Services.Agents.AgentJson.Options));

        // Terminal run → the never-ran slice must release.
        var run = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.WorkflowRun, r => r.Id == runId);
        run.Status = Messages.Enums.WorkflowRunStatus.Success;
        await db.SaveChangesAsync();

        var (settled, released, _) = await scope.Resolve<IBudgetSettlementService>().SweepAsync(batchSize: 100, CancellationToken.None);

        settled.ShouldBe(2, "both folded attempts settled (pessimistically at reserved — unknown model is unpriceable)");
        released.ShouldBe(1, "the never-ran slice on a terminal run releases its claim");
        (await ledger.CommittedUsdAsync(runId, teamId, CancellationToken.None)).ShouldBe(4m, "2+2 settled, the released slice returned its headroom");
    }

    private static async Task SeedDecisionAsync(CodeSpace.Core.Persistence.Db.CodeSpaceDbContext db, Guid runId, Guid teamId, int sequence, string kind, string payloadJson, string outcomeJson)
    {
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new CodeSpace.Core.Persistence.Entities.SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = kind, IdempotencyKey = $"{kind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = Messages.Agents.SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "budget-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = Infrastructure.WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    [Fact]
    public async Task Dangling_llm_reservations_reconcile_pessimistically_and_leave_the_live_set()
    {
        // W-hard slice 2: the llm:* kinds have no fact source to settle from — an orphan (teardown between
        // reserve and settle) must become TERMINAL bookkeeping without ever freeing the claim it held.
        var (teamId, userId) = await Infrastructure.WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var terminalRunId = await Infrastructure.WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        var ledger = scope.Resolve<IBudgetLedger>();

        // The seeded run must be TERMINAL for the terminal-run arm; a second, still-running run pins the negative.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET status = 'Success' WHERE id = {terminalRunId}");
        var runningRunId = Guid.NewGuid();

        (await ledger.ReserveAsync(terminalRunId, teamId, "llm:supervisor.decision", "orphan-1", 0.5m, 10m, "realized-v1", null, null, CancellationToken.None)).Admitted.ShouldBeTrue();
        (await ledger.ReserveAsync(runningRunId, teamId, "llm:supervisor.decision", "live-1", 0.5m, 10m, "realized-v1", null, null, CancellationToken.None)).Admitted.ShouldBeTrue();
        (await ledger.ReserveAsync(terminalRunId, teamId, "agent-attempt", "s1#a1", 0.5m, 10m, "realized-v1", null, null, CancellationToken.None)).Admitted.ShouldBeTrue();

        var committedBefore = await ledger.CommittedUsdAsync(terminalRunId, teamId, CancellationToken.None);

        var reconciled = await ledger.ReconcileDanglingAsync("llm:", batchSize: 50, CancellationToken.None);

        reconciled.ShouldBe(1, "exactly the terminal run's dangling llm row — the running run's live row and the agent-attempt kind are untouched");

        var rows = await db.BudgetReservation.AsNoTracking().Where(r => r.TeamId == teamId).ToListAsync();
        var orphan = rows.Single(r => r.ScopeKey == "orphan-1");
        orphan.State.ShouldBe(BudgetReservationStates.Reconciled, "the audit trail says decided-pessimistically, distinct from a fact-based settle");
        orphan.SettledUsd.ShouldBe(0.5m, "never lower than the claim it held");
        rows.Single(r => r.ScopeKey == "live-1").State.ShouldBe(BudgetReservationStates.Reserved);
        rows.Single(r => r.ScopeKey == "s1#a1").State.ShouldBe(BudgetReservationStates.Reserved, "the kind prefix scopes the pass — agent-attempt reconciliation stays fact-based");

        (await ledger.CommittedUsdAsync(terminalRunId, teamId, CancellationToken.None))
            .ShouldBe(committedBefore, "reconciliation is bookkeeping — headroom is never silently freed");

        // An INDETERMINATE llm row (the expiry sweep's output) reconciles too.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE budget_reservation SET state = 'Indeterminate' WHERE scope_key = 'live-1'");
        (await ledger.ReconcileDanglingAsync("llm:", 50, CancellationToken.None)).ShouldBe(1);
        (await db.BudgetReservation.AsNoTracking().SingleAsync(r => r.ScopeKey == "live-1")).State.ShouldBe(BudgetReservationStates.Reconciled);
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
