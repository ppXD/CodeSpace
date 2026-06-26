using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E5 fail-closed bounds + the GOAL config parse + the governance gate, pinned WITHOUT a DB.
/// Each bound is proven to FORCE-STOP at its limit with the right DISTINCT terminal reason — driven by a context
/// whose ledger-fact counters sit at the cap (the bound, not a decider, decides). The bound→reason vocabulary +
/// the new SupervisorLane consts are pinned (Rule 8). The ledger-counted property (a re-entry doesn't reset a
/// bound) is pinned at the service tier in <c>SupervisorBoundsServiceTests</c>; here we pin the PURE evaluator.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorBoundsTests
{
    // ── SupervisorGoalPlan: lenient parse + safe defaults + clamp ────────────────────

    [Fact]
    public void A_null_config_resolves_to_all_SupervisorLane_defaults()
    {
        var plan = SupervisorGoalPlan.From(null);

        plan.MaxRounds.ShouldBe(SupervisorLane.DecisionBudget, "no config → the historical decision budget (pre-E5 behaviour)");
        plan.MaxParallelism.ShouldBe(SupervisorGoalPlan.SpawnKCeiling);
        plan.MaxTotalSpawns.ShouldBe(SupervisorLane.DefaultMaxTotalSpawns);
        plan.MaxNoProgressDecisions.ShouldBe(SupervisorLane.DefaultMaxNoProgressDecisions);
        plan.ApprovalPolicy.ShouldBe(SupervisorApprovalPolicy.None, "no approval gate by default — pre-E5 behaviour");
    }

    [Fact]
    public void An_empty_config_object_resolves_to_defaults_too()
    {
        // A supervisor authored before E5 (goal only, no limits) must behave EXACTLY as today.
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { Goal = "ship it" });

        plan.MaxRounds.ShouldBe(SupervisorLane.DecisionBudget);
        plan.ApprovalPolicy.ShouldBe(SupervisorApprovalPolicy.None);
    }

    [Theory]
    [InlineData(10, 10)]   // an operator may TIGHTEN the budget
    [InlineData(0, 1)]     // below the floor → clamped to 1 (mirrors AdmissionController.ParseCap clamp semantics)
    [InlineData(999, SupervisorLane.DecisionBudget)]  // above the hard ceiling → clamped to the ceiling
    public void MaxRounds_is_clamped_to_the_decision_budget_ceiling(int configured, int expected)
    {
        SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxRounds = configured }).MaxRounds.ShouldBe(expected);

        // ...and an UNSET (null) value falls back to the default (distinct from an out-of-range explicit value).
        SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxRounds = null }).MaxRounds.ShouldBe(SupervisorLane.DecisionBudget);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(0, 1)]     // below the floor → clamped to 1
    [InlineData(5000, SupervisorLane.MaxTotalSpawnsCeiling)]
    public void MaxTotalSpawns_is_clamped(int configured, int expected)
    {
        SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxTotalSpawns = configured }).MaxTotalSpawns.ShouldBe(expected);

        SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxTotalSpawns = null }).MaxTotalSpawns.ShouldBe(SupervisorLane.DefaultMaxTotalSpawns);
    }

    [Theory]
    [InlineData("spawns", SupervisorApprovalPolicy.Spawns)]
    [InlineData("side-effects", SupervisorApprovalPolicy.Spawns)]
    [InlineData("SPAWNS", SupervisorApprovalPolicy.Spawns)]
    [InlineData("none", SupervisorApprovalPolicy.None)]
    [InlineData("", SupervisorApprovalPolicy.None)]
    [InlineData(null, SupervisorApprovalPolicy.None)]
    [InlineData("garbage", SupervisorApprovalPolicy.None)]
    public void ApprovalPolicy_parses_leniently(string? raw, SupervisorApprovalPolicy expected)
    {
        SupervisorGoalPlan.From(new SupervisorGoalConfig { ApprovalPolicy = raw }).ApprovalPolicy.ShouldBe(expected);
    }

    [Fact]
    public void MaxCostUsd_is_null_by_default_so_a_pre_SOTA4_supervisor_has_no_cost_cap()
    {
        // No cap authored → the run is bounded by the agent-COUNT cap alone (SOTA #4 fail-open default).
        SupervisorGoalPlan.From(null).MaxCostUsd.ShouldBeNull();
        SupervisorGoalPlan.From(new SupervisorGoalConfig { Goal = "ship it" }).MaxCostUsd.ShouldBeNull();
    }

    [Theory]
    [InlineData(12.50, 12.50)]   // a positive budget is kept verbatim (no clamp ceiling — cost can't run away past the count cap)
    [InlineData(0.0, null)]      // a zero budget resolves to null (no budget — would otherwise block the first spawn before any spend is known)
    [InlineData(-5.0, null)]     // a negative budget resolves to null (no budget, not a zero budget)
    public void MaxCostUsd_normalizes_non_positive_to_no_cap(double configured, double? expected)
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxCostUsd = (decimal)configured });

        plan.MaxCostUsd.ShouldBe(expected is { } e ? (decimal)e : null);
    }

    // ── PRE-DECISION bounds: each force-STOP reason at its limit ──────────────────────

    [Fact]
    public void Depth_cap_force_stops_a_supervisor_nested_too_deep()
    {
        var plan = SupervisorGoalPlan.From(null);

        SupervisorBounds.PreDecision(Context(turn: 0), plan, supervisorDepth: SupervisorLane.MaxSupervisorDepth)
            .ShouldBe(SupervisorStopReasons.DepthCapExceeded);

        SupervisorBounds.PreDecision(Context(turn: 0), plan, supervisorDepth: SupervisorLane.MaxSupervisorDepth - 1)
            .ShouldBeNull("one below the depth cap proceeds");
    }

    [Fact]
    public void Round_budget_force_stops_at_the_max_rounds_limit()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxRounds = 5 });

        SupervisorBounds.PreDecision(Context(turn: 5), plan, supervisorDepth: 0).ShouldBe(SupervisorStopReasons.BudgetExhausted);
        SupervisorBounds.PreDecision(Context(turn: 4), plan, supervisorDepth: 0).ShouldBeNull("turn 4 of a 5-round budget still proceeds");
    }

    [Fact]
    public void No_progress_guard_force_stops_after_the_streak_cap()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxNoProgressDecisions = 3 });

        SupervisorBounds.PreDecision(Context(turn: 10, noProgress: 3), plan, supervisorDepth: 0).ShouldBe(SupervisorStopReasons.NoProgress);
        SupervisorBounds.PreDecision(Context(turn: 10, noProgress: 2), plan, supervisorDepth: 0).ShouldBeNull("under the no-progress cap proceeds");
    }

    [Fact]
    public void Pre_decision_precedence_is_depth_then_budget_then_no_progress()
    {
        // All three trip at once → depth wins (the deterministic precedence a re-entry re-derives).
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxRounds = 1, MaxNoProgressDecisions = 1 });

        SupervisorBounds.PreDecision(Context(turn: 30, noProgress: 30), plan, supervisorDepth: SupervisorLane.MaxSupervisorDepth)
            .ShouldBe(SupervisorStopReasons.DepthCapExceeded);
    }

    // ── POST-DECISION bounds: spawn-K cap + total-spawn cap ───────────────────────────

    [Fact]
    public void Spawn_fan_out_over_the_parallelism_cap_is_refused()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxParallelism = 2 });

        SupervisorBounds.PostDecision(Context(turn: 1), plan, Spawn("a", "b", "c")).ShouldBe(SupervisorStopReasons.SpawnFanOutExceedsCap);
        SupervisorBounds.PostDecision(Context(turn: 1), plan, Spawn("a", "b")).ShouldBeNull("a spawn at the cap proceeds");
    }

    [Fact]
    public void Total_spawn_cap_force_stops_when_a_further_spawn_would_breach_it()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxTotalSpawns = 4 });

        // Already spawned 3; a spawn of 2 → 5 > 4 → refused.
        SupervisorBounds.PostDecision(Context(turn: 3, totalSpawned: 3), plan, Spawn("x", "y")).ShouldBe(SupervisorStopReasons.TotalSpawnCapReached);

        // A spawn of exactly 1 → 4 == cap → proceeds (the cap is the max, hit exactly).
        SupervisorBounds.PostDecision(Context(turn: 3, totalSpawned: 3), plan, Spawn("x")).ShouldBeNull("spawning up to exactly the cap is allowed");
    }

    [Fact]
    public void Total_spawn_cap_counts_a_retry_as_one_spawn()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxTotalSpawns = 2 });

        SupervisorBounds.PostDecision(Context(turn: 1, totalSpawned: 2), plan, Retry()).ShouldBe(SupervisorStopReasons.TotalSpawnCapReached);
    }

    [Fact]
    public void Non_side_effecting_decisions_are_never_post_bounded()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxTotalSpawns = 1 });

        SupervisorBounds.PostDecision(Context(turn: 1, totalSpawned: 50), plan, Plan()).ShouldBeNull("a plan creates no agents");
        SupervisorBounds.PostDecision(Context(turn: 1, totalSpawned: 50), plan, Stop()).ShouldBeNull("a stop creates no agents");
    }

    // ── POST-DECISION cost cap (SOTA #4): realized-spend backpressure ─────────────────

    [Fact]
    public void Cost_cap_force_stops_the_next_spend_when_realized_spend_exceeds_the_budget()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxCostUsd = 10m });

        // Realized spend ABOVE the budget → the next spend-incurring decision is refused.
        SupervisorBounds.PostDecision(Context(turn: 2, runSpend: 10.01m), plan, Spawn("a")).ShouldBe(SupervisorStopReasons.CostCapReached);
        SupervisorBounds.PostDecision(Context(turn: 2, runSpend: 25m), plan, Retry()).ShouldBe(SupervisorStopReasons.CostCapReached, "a retry is a spend too");
    }

    [Fact]
    public void Cost_cap_proceeds_at_or_below_the_budget_strict_greater_than()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxCostUsd = 10m });

        // STRICT > matches the total-spawn convention: exactly-at-budget still proceeds (salvage already-paid work).
        SupervisorBounds.PostDecision(Context(turn: 2, runSpend: 10m), plan, Spawn("a")).ShouldBeNull("exactly at the budget proceeds");
        SupervisorBounds.PostDecision(Context(turn: 2, runSpend: 9.99m), plan, Spawn("a")).ShouldBeNull("under the budget proceeds");
        SupervisorBounds.PostDecision(Context(turn: 0, runSpend: 0m), plan, Spawn("a")).ShouldBeNull("no spend yet → the first spawn is never blocked (fail-open)");
    }

    [Fact]
    public void No_cost_cap_never_force_stops_on_cost_however_large_the_spend()
    {
        // MaxCostUsd null (the default — no budget authored) → cost never trips, only the count cap bounds the run.
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxTotalSpawns = 50 });
        plan.MaxCostUsd.ShouldBeNull();

        SupervisorBounds.PostDecision(Context(turn: 5, totalSpawned: 1, runSpend: 9_999m), plan, Spawn("a")).ShouldBeNull();
    }

    [Fact]
    public void Spend_is_only_metered_on_side_effecting_decisions()
    {
        // The cost bound, like the count caps, only refuses a spawn/retry — a plan/merge/stop spends no agent tokens.
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxCostUsd = 1m });

        SupervisorBounds.PostDecision(Context(turn: 1, runSpend: 1_000m), plan, Plan()).ShouldBeNull("a plan incurs no agent spend");
        SupervisorBounds.PostDecision(Context(turn: 1, runSpend: 1_000m), plan, Stop()).ShouldBeNull("a stop incurs no agent spend");
    }

    [Fact]
    public void Count_caps_take_precedence_over_the_cost_cap()
    {
        // Both the total-spawn cap AND the cost cap are breached → the count cap reason wins (it is checked first),
        // so a re-entry re-derives the SAME deterministic stop regardless of which the operator reads as "the" cause.
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxTotalSpawns = 1, MaxCostUsd = 1m });

        SupervisorBounds.PostDecision(Context(turn: 2, totalSpawned: 1, runSpend: 1_000m), plan, Spawn("a"))
            .ShouldBe(SupervisorStopReasons.TotalSpawnCapReached, "the count cap is checked before the cost cap");
    }

    // ── Spawn-K schema cap pinned + the runtime guard matches it ──────────────────────

    [Fact]
    public void The_spawn_k_ceiling_matches_the_schema_max_items()
    {
        // The runtime fan-out cap must equal the schema's maxItems so a schema-bypassing decider can't fan out
        // wider than the model is allowed to ask for. Pin both literals (Rule 8).
        SupervisorGoalPlan.SpawnKCeiling.ShouldBe(20);

        var schema = Core.Services.Supervisor.Deciders.SupervisorDecisionSchema.ResponseSchema;
        var spawnMaxItems = schema.GetProperty("properties").GetProperty("spawn").GetProperty("properties").GetProperty("subtaskIds").GetProperty("maxItems").GetInt32();
        spawnMaxItems.ShouldBe(SupervisorGoalPlan.SpawnKCeiling, "the schema maxItems and the runtime cap must agree");
    }

    // ── Pins (Rule 8) ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_E5_consts_are_pinned()
    {
        SupervisorLane.DefaultMaxTotalSpawns.ShouldBe(50);
        SupervisorLane.MaxTotalSpawnsCeiling.ShouldBe(1_000);
        SupervisorLane.DefaultMaxNoProgressDecisions.ShouldBe(8);
        SupervisorLane.MaxSupervisorDepth.ShouldBe(8);
    }

    [Fact]
    public void The_terminal_reasons_are_pinned()
    {
        // Surfaced as the node's terminal reason + load-bearing for the deterministic re-derived stop. A rename
        // changes what an operator sees — pin the literals (Rule 8).
        SupervisorStopReasons.BudgetExhausted.ShouldBe("budget exhausted");
        SupervisorStopReasons.TotalSpawnCapReached.ShouldBe("total spawn cap reached");
        SupervisorStopReasons.CostCapReached.ShouldBe("cost cap reached");
        SupervisorStopReasons.SpawnFanOutExceedsCap.ShouldBe("spawn fan-out exceeds cap");
        SupervisorStopReasons.DepthCapExceeded.ShouldBe("supervisor nesting cap exceeded");
        SupervisorStopReasons.NoProgress.ShouldBe("no progress");
        SupervisorStopReasons.GovernanceDenied.ShouldBe("governance denied the side effect");
        SupervisorStopReasons.PlanInvalid.ShouldBe("plan structurally invalid");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static SupervisorTurnContext Context(int turn, int totalSpawned = 0, int noProgress = 0, decimal runSpend = 0m) =>
        new() { Goal = "g", TurnNumber = turn, TotalSpawnedAgents = totalSpawned, NoProgressDecisions = noProgress, RunSpendUsd = runSpend };

    private static SupervisorDecision Spawn(params string[] ids) => new()
    {
        Kind = SupervisorDecisionKinds.Spawn,
        PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = ids }, AgentJson.Options),
    };

    private static SupervisorDecision Retry() => new()
    {
        Kind = SupervisorDecisionKinds.Retry,
        PayloadJson = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = "s" }, AgentJson.Options),
    };

    private static SupervisorDecision Plan() => new() { Kind = SupervisorDecisionKinds.Plan, PayloadJson = "{}" };

    private static SupervisorDecision Stop() => new() { Kind = SupervisorDecisionKinds.Stop, PayloadJson = """{"reason":"done"}""" };
}
