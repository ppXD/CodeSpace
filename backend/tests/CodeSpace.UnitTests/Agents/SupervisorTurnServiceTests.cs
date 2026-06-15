using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.UnitTests.Infrastructure;
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
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":["a"]}""", """{"planned":["a"]}""");

        var context = await Service(ledger).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", goalConfig: null, CancellationToken.None);

        context.TurnNumber.ShouldBe(1, "one decided decision → the next turn is turn 1");
        context.PriorDecisions.Count.ShouldBe(1);
        context.PriorDecisions[0].DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        context.PriorDecisions[0].OutcomeJson.ShouldBe("""{"planned":["a"]}""", "the terminal outcome is replayed, not re-derived");
        context.InFlight.ShouldBeNull();
    }

    [Fact]
    public async Task Rehydrate_identifies_the_one_in_flight_decision()
    {
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":["a"]}""", """{"planned":["a"]}""");
        ledger.SeedPending(_runId, _teamId, SupervisorDecisionKinds.Stop, """{"reason":"x"}""");

        var context = await Service(ledger).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", goalConfig: null, CancellationToken.None);

        context.TurnNumber.ShouldBe(1, "an in-flight (non-terminal) row is NOT a decided decision");
        context.InFlight.ShouldNotBeNull();
        context.InFlight!.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
    }

    // ── The turn loop: turn 1 plan → park; turn 2 stop → finish ──────────────────────

    [Fact]
    public async Task Turn1_plans_and_parks_then_turn2_stops_and_finishes()
    {
        var ledger = new FakeSupervisorDecisionLog();
        var service = Service(ledger);

        var turn1 = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        turn1.IsFinished.ShouldBeFalse("a plan parks for the next turn");
        turn1.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        turn1.NextTurn!.TurnNumber.ShouldBe(1, "the park carries the next turn's number");
        ledger.Rows.Count.ShouldBe(1, "exactly one decision recorded");
        ledger.Rows[0].Status.ShouldBe(SupervisorDecisionStatus.Succeeded);

        var turn2 = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        turn2.IsFinished.ShouldBeTrue("a stop finishes the loop");
        turn2.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        ledger.Rows.Count.ShouldBe(2, "the ledger has exactly two rows in Sequence order");
        ledger.Rows.Select(r => r.DecisionKind).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop });
    }

    // ── Budget exhausted → forced terminal stop (fail-closed, counted from the ledger) ──

    [Fact]
    public async Task Budget_exhausted_forces_a_clean_terminal_stop()
    {
        var ledger = new FakeSupervisorDecisionLog();

        // Seed DecisionBudget decided decisions so TurnNumber == budget → the decider is never asked.
        for (var i = 0; i < SupervisorLane.DecisionBudget; i++)
            ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, $$"""{"turn":{{i}}}""", "{}");

        // A decider that would NEVER stop on its own — proving the budget, not the decider, terminates.
        var service = new SupervisorTurnService(ledger, new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the budget forces a terminal stop");
        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        result.TerminalReason.ShouldBe(SupervisorTurnService.BudgetExhaustedReason);
    }

    // ── Governance DENY → force-STOP=GovernanceDenied, no agent staged (the fail-closed branch) ──

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    public void A_governance_denied_side_effecting_decision_force_stops_and_stages_no_agent(string kind)
    {
        // Drive the REAL Deny wiring (SupervisorTurnService.GateSideEffectingDecision) end-to-end, not its two
        // ingredients in isolation. The branch is unreachable from operator config today (ParseApprovalPolicy
        // clamps every unknown policy to None), so we inject the unmapped policy directly into the gate's context
        // — the same forward-compat exposure a future irreversible/merge-PR policy would open. Asserts the gate
        // turns the denied side effect into a force-STOP carrying the GovernanceDenied reason and stages NO agent.
        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new StubSupervisorDecider(), executor, db: null!, NullLogger<SupervisorTurnService>.Instance);

        var context = new SupervisorTurnContext { Goal = "goal", TurnNumber = 0, ApprovalPolicy = (SupervisorApprovalPolicy)999 };
        var spawn = new SupervisorDecision { Kind = kind, PayloadJson = """{"subtaskIds":["a","b"]}""" };

        var gated = service.GateSideEffectingDecision(context, spawn);
        var result = SupervisorTurnService.BuildResult(context, gated, SupervisorExecution.Synchronous("{}"));

        result.IsFinished.ShouldBeTrue("an unmapped policy → Confined → the gate DENIES → the turn force-STOPS");
        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        result.TerminalReason.ShouldBe(SupervisorStopReasons.GovernanceDenied, "the DENY branch surfaces the distinct, operator-legible governance-refused reason");
        executor.Calls.ShouldBe(0, "the gate refused BEFORE any execute — no agent was staged");
    }

    // ── Replay: a re-run of a settled turn does NOT re-execute the side effect ───────

    [Fact]
    public async Task Replaying_a_settled_turn_does_not_double_execute()
    {
        var ledger = new FakeSupervisorDecisionLog();
        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, new StubSupervisorDecider(), executor, db: null!, NullLogger<SupervisorTurnService>.Instance);

        // First pass: turn 0 (plan) executes once + records terminal.
        await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);
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

    private SupervisorTurnService Service(FakeSupervisorDecisionLog ledger) =>
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
}
