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

    // ── P1-1 CROWN JEWEL: a crashed in-flight decision REPLAYS FROZEN, independent of decider determinism ──

    [Fact]
    public async Task Replaying_an_in_flight_turn_re_executes_the_frozen_decision_even_when_the_decider_is_non_deterministic()
    {
        // Simulate a crash mid-execution: turn 0's decision A (a plan) was claimed (INSERTed Pending) by a prior
        // walk that crashed BEFORE recording terminal — so the ledger holds ONE non-terminal A row. On re-entry
        // RehydrateFromDecisionLog folds it into context.InFlight; the fix replays A FROZEN (decider + bounds NOT
        // re-run) UNDER A's existing claim id (win the still-Pending begin-CAS → execute once → record terminal).
        // The decider here is NON-DETERMINISTIC — it would emit a DIFFERENT decision B on this turn. WITHOUT the
        // fix RunTurnAsync would ask the decider, get B, derive B's DIFFERENT key, find no match, INSERT a 2nd row,
        // execute B, and STRAND the A row forever. WITH the fix the decider is never consulted on replay, so its B
        // output is irrelevant and the ledger stays a single A row that the recovery finishes.
        var ledger = new FakeSupervisorDecisionLog();
        var plannedA = """{"subtasks":["a"]}""";
        ledger.SeedPending(_runId, _teamId, SupervisorDecisionKinds.Plan, plannedA);
        var inFlightId = ledger.Rows[0].Id;

        var decider = new NonDeterministicDecider(SupervisorDecisionKinds.Plan, plannedA, SupervisorDecisionKinds.Stop, """{"reason":"divergent-B"}""");
        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, decider, executor, db: null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan, "the REPLAYED decision is the frozen in-flight A (plan), NOT the decider's divergent B (stop)");
        result.IsFinished.ShouldBeFalse("A is a plan → the turn self-advances, NOT B's stop");

        decider.CallCount.ShouldBe(0, "the decider was NOT consulted to produce the replayed decision — replay is frozen, independent of decider determinism");
        executor.Calls.ShouldBe(1, "the frozen A side effect re-executed exactly once on recovery");

        ledger.Rows.Count.ShouldBe(1, "EXACTLY ONE row for the turn — no divergent B row, no stranded A (without the fix the decider's B would derive a different key → a 2nd row + a strand)");
        ledger.Rows[0].Id.ShouldBe(inFlightId, "the single row is the SAME in-flight A row, re-executed under its existing claim");
        ledger.Rows[0].DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        ledger.Rows[0].PayloadJson.ShouldBe(plannedA, "the single row is A's frozen payload");
        ledger.Rows[0].Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the crashed A row reached terminal on recovery — no strand");
    }

    private SupervisorTurnService Service(FakeSupervisorDecisionLog ledger) =>
        new(ledger, new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: null!, NullLogger<SupervisorTurnService>.Instance);

    /// <summary>A decider that emits decision A on its FIRST call and a DIFFERENT decision B on every later one — models a non-deterministic real LLM re-asked on the same turn after a crash. The crown-jewel test asserts the replay ignores it entirely (CallCount stays 0).</summary>
    private sealed class NonDeterministicDecider : ISupervisorDecider
    {
        private readonly SupervisorDecision _first;
        private readonly SupervisorDecision _later;

        public NonDeterministicDecider(string firstKind, string firstPayload, string laterKind, string laterPayload)
        {
            _first = new SupervisorDecision { Kind = firstKind, PayloadJson = firstPayload };
            _later = new SupervisorDecision { Kind = laterKind, PayloadJson = laterPayload };
        }

        public int CallCount { get; private set; }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(CallCount == 1 ? _first : _later);
        }
    }

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
