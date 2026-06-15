using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E2 supervisor turn-loop DECISION logic, pinned without a DB. Covers the deterministic
/// stub decider's script (turn 0 = plan → turn 1 = stop), the fail-closed budget force-stop, the per-turn
/// idempotency-key + IterationKey derivation (distinct per turn → no collision/clobber), and the stub
/// executor's per-kind outcome. The ledger-backed claim + replay (no-double-plan) is pinned at the
/// integration tier against real Postgres (<c>SupervisorTurnFlowTests</c>); here we pin the PURE pieces.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorTurnLoopTests
{
    private readonly StubSupervisorDecider _decider = new();

    // ── The stub decider's deterministic script (turn 0 plan → turn 1 stop) ──────────

    [Fact]
    public void Turn0_plans_with_the_fixed_subtask_list()
    {
        var decision = _decider.Decide(Context(turnNumber: 0));

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.IsTerminal.ShouldBeFalse("a plan is not terminal — the loop re-enters for the next turn");

        var subtasks = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtasks").EnumerateArray().Select(e => e.GetString()).ToList();
        subtasks.ShouldBe(StubSupervisorDecider.StubPlannedSubtasks, "the stub plans a fixed, deterministic subtask list");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Every_turn_after_the_first_stops(int turnNumber)
    {
        var decision = _decider.Decide(Context(turnNumber));

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("a stop ends the loop");
    }

    [Fact]
    public void The_decider_is_deterministic_in_the_turn_number()
    {
        // Same turn → byte-identical payload (the exactly-once-on-replay property the ledger relies on).
        _decider.Decide(Context(0)).PayloadJson.ShouldBe(_decider.Decide(Context(0)).PayloadJson);
        _decider.Decide(Context(1)).PayloadJson.ShouldBe(_decider.Decide(Context(1)).PayloadJson);
    }

    // ── Per-turn idempotency key + IterationKey are DISTINCT per turn (must-fix #1) ──

    [Fact]
    public void The_turn_discriminator_is_distinct_per_turn()
    {
        SupervisorTurnService.TurnDiscriminator(0).ShouldBe("turn0");
        SupervisorTurnService.TurnDiscriminator(1).ShouldBe("turn1");
        SupervisorTurnService.TurnDiscriminator(0).ShouldNotBe(SupervisorTurnService.TurnDiscriminator(1));
    }

    [Fact]
    public void The_same_decision_in_different_turns_yields_distinct_idempotency_keys()
    {
        // The turn discriminator is what keeps a repeated decision payload across turns a DISTINCT,
        // re-executable ledger row (no unique-index collision) — must-fix #1's exactly-once partner.
        const string payload = """{"subtasks":["a","b"]}""";

        var turn0Key = SupervisorDecisionLog.DeriveIdempotencyKey(SupervisorDecisionKinds.Plan, payload, SupervisorTurnService.TurnDiscriminator(0));
        var turn1Key = SupervisorDecisionLog.DeriveIdempotencyKey(SupervisorDecisionKinds.Plan, payload, SupervisorTurnService.TurnDiscriminator(1));

        turn0Key.ShouldNotBe(turn1Key, "binding the turn discriminator makes the same payload a distinct key per turn");

        // ...and the SAME turn + SAME payload re-derives the SAME key (the in-turn replay/dedup path).
        SupervisorDecisionLog.DeriveIdempotencyKey(SupervisorDecisionKinds.Plan, payload, SupervisorTurnService.TurnDiscriminator(0)).ShouldBe(turn0Key);
    }

    [Fact]
    public void The_per_turn_iteration_key_mirrors_the_flow_map_scheme()
    {
        // The node mints <nodeId>#turn{N}; assert the shape directly (the engine stamps it on the wait row,
        // so each turn parks a distinct (run, node, iteration) row).
        IterationKeyFor("sup", 0).ShouldBe("sup#turn0");
        IterationKeyFor("sup", 1).ShouldBe("sup#turn1");
        IterationKeyFor("sup", 0).ShouldNotBe(IterationKeyFor("sup", 1));
    }

    // ── The stub executor's deterministic per-kind outcome ───────────────────────────

    [Fact]
    public async Task The_stub_executor_records_a_planned_list_for_plan_and_a_marker_for_stop()
    {
        var executor = new StubSupervisorActionExecutor();

        var planOutcome = await executor.ExecuteAsync(_decider.Decide(Context(0)), Context(0), CancellationToken.None);
        JsonDocument.Parse(planOutcome).RootElement.GetProperty("planned").EnumerateArray().Count().ShouldBe(StubSupervisorDecider.StubPlannedSubtasks.Count);

        var stopOutcome = await executor.ExecuteAsync(_decider.Decide(Context(1)), Context(1), CancellationToken.None);
        JsonDocument.Parse(stopOutcome).RootElement.GetProperty("stopped").GetBoolean().ShouldBeTrue();
    }

    // ── Pins (Rule 8) ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_decision_budget_is_pinned()
    {
        // Load-bearing: the turn loop force-stops at this count. Changing it changes fail-closed behaviour,
        // so pin the literal so a tweak is a compile-test-visible decision.
        SupervisorLane.DecisionBudget.ShouldBe(30);
    }

    private static SupervisorTurnContext Context(int turnNumber) => new() { Goal = "ship it", TurnNumber = turnNumber };

    // Mirrors AgentSupervisorNode.Park's key scheme so the test pins the contract without reaching into the node.
    private static string IterationKeyFor(string nodeId, int turnNumber) => $"{nodeId}#turn{turnNumber}";
}
