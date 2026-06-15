using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E2 <see cref="SupervisorTurnService"/> orchestration, driven against an in-memory fake
/// <see cref="ISupervisorDecisionLog"/> (a faithful model of the E1 unique-index dedup + Pending→Running
/// claim + terminal record — the real ledger is pinned over Postgres at the integration tier). Pins:
/// RehydrateFromDecisionLog folds a terminal decision + identifies the in-flight one; turn 1 plans + parks
/// with TurnNumber+1; turn 2 stops + finishes; budget-exhausted forces a clean terminal stop; a replay
/// (re-running a turn whose decision already settled) does NOT re-execute the side effect (no double-plan).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorTurnServiceTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();

    // ── RehydrateFromDecisionLog: replay terminal, identify in-flight ────────────────

    [Fact]
    public async Task Rehydrate_folds_a_terminal_decision_and_sets_the_turn_number()
    {
        var ledger = new FakeLedger();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":["a"]}""", """{"planned":["a"]}""");

        var context = await Service(ledger).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", CancellationToken.None);

        context.TurnNumber.ShouldBe(1, "one decided decision → the next turn is turn 1");
        context.PriorDecisions.Count.ShouldBe(1);
        context.PriorDecisions[0].DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        context.PriorDecisions[0].OutcomeJson.ShouldBe("""{"planned":["a"]}""", "the terminal outcome is replayed, not re-derived");
        context.InFlight.ShouldBeNull();
    }

    [Fact]
    public async Task Rehydrate_identifies_the_one_in_flight_decision()
    {
        var ledger = new FakeLedger();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":["a"]}""", """{"planned":["a"]}""");
        ledger.SeedPending(_runId, _teamId, SupervisorDecisionKinds.Stop, """{"reason":"x"}""");

        var context = await Service(ledger).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", CancellationToken.None);

        context.TurnNumber.ShouldBe(1, "an in-flight (non-terminal) row is NOT a decided decision");
        context.InFlight.ShouldNotBeNull();
        context.InFlight!.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
    }

    // ── The turn loop: turn 1 plan → park; turn 2 stop → finish ──────────────────────

    [Fact]
    public async Task Turn1_plans_and_parks_then_turn2_stops_and_finishes()
    {
        var ledger = new FakeLedger();
        var service = Service(ledger);

        var turn1 = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, CancellationToken.None);

        turn1.IsFinished.ShouldBeFalse("a plan parks for the next turn");
        turn1.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        turn1.NextTurn!.TurnNumber.ShouldBe(1, "the park carries the next turn's number");
        ledger.Rows.Count.ShouldBe(1, "exactly one decision recorded");
        ledger.Rows[0].Status.ShouldBe(SupervisorDecisionStatus.Succeeded);

        var turn2 = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, CancellationToken.None);

        turn2.IsFinished.ShouldBeTrue("a stop finishes the loop");
        turn2.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        ledger.Rows.Count.ShouldBe(2, "the ledger has exactly two rows in Sequence order");
        ledger.Rows.Select(r => r.DecisionKind).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop });
    }

    // ── Budget exhausted → forced terminal stop (fail-closed, counted from the ledger) ──

    [Fact]
    public async Task Budget_exhausted_forces_a_clean_terminal_stop()
    {
        var ledger = new FakeLedger();

        // Seed DecisionBudget decided decisions so TurnNumber == budget → the decider is never asked.
        for (var i = 0; i < SupervisorLane.DecisionBudget; i++)
            ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, $$"""{"turn":{{i}}}""", "{}");

        // A decider that would NEVER stop on its own — proving the budget, not the decider, terminates.
        var service = new SupervisorTurnService(ledger, new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the budget forces a terminal stop");
        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        result.TerminalReason.ShouldBe(SupervisorTurnService.BudgetExhaustedReason);
    }

    // ── Replay: a re-run of a settled turn does NOT re-execute the side effect ───────

    [Fact]
    public async Task Replaying_a_settled_turn_does_not_double_execute()
    {
        var ledger = new FakeLedger();
        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, new StubSupervisorDecider(), executor, db: null!, NullLogger<SupervisorTurnService>.Instance);

        // First pass: turn 0 (plan) executes once + records terminal.
        await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, CancellationToken.None);
        executor.Calls.ShouldBe(1);
        ledger.Rows.Count.ShouldBe(1);

        // SIMULATE A REPLAY of turn 0 at the SAME turn number — re-derive the same per-turn key. The unique
        // index dedups to the prior terminal row (Duplicate) → the side effect is NOT re-run (no double-plan).
        var replayContext = new SupervisorTurnContext { Goal = "goal", TurnNumber = 0 };
        var decision = await new StubSupervisorDecider().DecideAsync(replayContext, CancellationToken.None);
        var key = SupervisorDecisionLog.DeriveIdempotencyKey(decision.Kind, decision.PayloadJson, SupervisorTurnService.TurnDiscriminator(0));
        var claim = await ledger.TryClaimAsync(_runId, _teamId, decision.Kind, key, "h", decision.PayloadJson, 0, CancellationToken.None);

        claim.Outcome.ShouldBe(SupervisorDecisionClaimOutcome.Duplicate, "the same per-turn key collides → replay the prior outcome");
        executor.Calls.ShouldBe(1, "the side effect ran exactly once — no double-plan on replay");
        ledger.Rows.Count.ShouldBe(1, "still exactly one row");
    }

    private SupervisorTurnService Service(FakeLedger ledger) =>
        new(ledger, new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: null!, NullLogger<SupervisorTurnService>.Instance);

    /// <summary>A decider that always plans — used to prove the budget (not the decider) is what terminates a runaway loop.</summary>
    private sealed class AlwaysPlanDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = """{"x":1}""" });
    }

    /// <summary>Counts ExecuteAsync calls — proves the side effect runs exactly once (no double-execute on replay).</summary>
    private sealed class CountingExecutor : ISupervisorActionExecutor
    {
        public int Calls { get; private set; }

        public Task<SupervisorExecution> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(SupervisorExecution.Synchronous("{}"));
        }
    }

    /// <summary>
    /// An in-memory <see cref="ISupervisorDecisionLog"/> faithfully modelling the E1 invariants the loop relies on:
    /// a unique <c>(run, key)</c> index (a duplicate claim returns the prior terminal / in-flight, never a 2nd row),
    /// a Pending → Running execution claim (single-winner), and a terminal record. Sequence is insertion order.
    /// </summary>
    private sealed class FakeLedger : ISupervisorDecisionLog
    {
        public List<SupervisorDecisionRecord> Rows { get; } = new();
        private long _seq;

        public void SeedTerminal(Guid runId, Guid teamId, string kind, string payloadJson, string outcomeJson) =>
            Rows.Add(new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = ++_seq, DecisionKind = kind, IdempotencyKey = $"{kind}:{Rows.Count}", InputHash = "h", PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Succeeded, OutcomeJson = outcomeJson });

        public void SeedPending(Guid runId, Guid teamId, string kind, string payloadJson) =>
            Rows.Add(new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = ++_seq, DecisionKind = kind, IdempotencyKey = $"{kind}:{Rows.Count}", InputHash = "h", PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Pending });

        public Task<SupervisorDecisionClaim> TryClaimAsync(Guid supervisorRunId, Guid teamId, string decisionKind, string idempotencyKey, string inputHash, string payloadJson, long fenceEpoch, CancellationToken cancellationToken)
        {
            var existing = Rows.FirstOrDefault(r => r.SupervisorRunId == supervisorRunId && r.IdempotencyKey == idempotencyKey);

            if (existing != null)
                return Task.FromResult(SupervisorDecisionStateMachine.IsTerminal(existing.Status)
                    ? SupervisorDecisionClaim.Duplicate(existing.Id, existing.Status, existing.OutcomeJson, existing.Error)
                    : SupervisorDecisionClaim.InFlight(existing.Id));

            var row = new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = supervisorRunId, Sequence = ++_seq, DecisionKind = decisionKind, IdempotencyKey = idempotencyKey, InputHash = inputHash, PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Pending, FenceEpoch = fenceEpoch };
            Rows.Add(row);
            return Task.FromResult(SupervisorDecisionClaim.Proceed(row.Id));
        }

        public Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken)
        {
            var row = Rows.Single(r => r.Id == decisionId);

            if (row.Status != SupervisorDecisionStatus.Pending) return Task.FromResult(false);

            row.Status = SupervisorDecisionStatus.Running;
            return Task.FromResult(true);
        }

        public Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken)
        {
            var row = Rows.Single(r => r.Id == decisionId);
            row.Status = status;
            row.OutcomeJson = outcomeJson;
            row.Error = error;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupervisorDecisionRecord>>(Rows.Where(r => r.SupervisorRunId == supervisorRunId && r.TeamId == teamId).OrderBy(r => r.Sequence).ToList());

        public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken)
        {
            var row = Rows.SingleOrDefault(r => r.Id == decisionId && r.TeamId == teamId);
            if (row != null) row.OutcomeJson = foldedOutcomeJson;
            return Task.CompletedTask;
        }

        public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
