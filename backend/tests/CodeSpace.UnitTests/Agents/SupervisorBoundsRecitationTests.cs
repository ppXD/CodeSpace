using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins P5-3 — the RUN BOUNDS recitation. The no-progress streak and the total-spawn count both
/// force-stop a run (<see cref="SupervisorBounds"/>), but the decider previously saw NEITHER — it could only
/// infer the streak from a post-hoc tier-escalation note, the exact blindness behind evidence-less spawn/retry
/// loops marching a run into its kill. Pins: the pure render (null at zero — a fresh run's prompt is
/// byte-identical; the streak line teaches what SETTLED EVIDENCE means; the runway counts down with a singular
/// last-decision form; the spawn line falls back to the lane-default cap) and the prompt wiring.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorBoundsRecitationTests
{
    // ── The pure render ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_run_renders_nothing()
    {
        SupervisorBoundsRecitation.Render(noProgressDecisions: 0, maxNoProgressDecisions: 8, totalSpawnedAgents: 0, maxTotalSpawns: 50)
            .ShouldBeNull("both counters at zero → no block, the healthy young run pays no prompt tax");
    }

    [Fact]
    public void The_streak_line_names_the_count_the_cap_and_what_evidence_means()
    {
        var block = SupervisorBoundsRecitation.Render(6, 8, 0, 50);

        block.ShouldNotBeNull();
        block!.ShouldContain("RUN BOUNDS", Case.Sensitive);
        block.ShouldContain("no-progress decisions: 6 of 8", Case.Sensitive);
        block.ShouldContain("2 more evidence-less decisions force-stop this run", Case.Sensitive, "the runway is explicit — no arithmetic left to the model");
        block.ShouldContain("SETTLED EVIDENCE", Case.Sensitive, "the reset rule is taught, not assumed");
        block.ShouldContain("stop honestly or ask a human now", Case.Sensitive, "the honest exits are named alongside the evidence steer");
    }

    [Fact]
    public void The_last_decision_runway_reads_singular()
    {
        SupervisorBoundsRecitation.Render(7, 8, 0, 50)!
            .ShouldContain("ONE more evidence-less decision force-stops this run", Case.Sensitive);
    }

    [Fact]
    public void The_spawn_line_renders_the_count_against_the_cap()
    {
        var block = SupervisorBoundsRecitation.Render(0, 8, 5, 40);

        block.ShouldNotBeNull("spawned agents alone warrant the block even with a clean streak");
        block!.ShouldContain("agents spawned: 5 of 40 total-spawn cap", Case.Sensitive);
        block.ShouldNotContain("no-progress decisions", Case.Sensitive, "a zero streak renders no streak line");
    }

    [Fact]
    public void A_legacy_context_without_a_cap_falls_back_to_the_lane_default()
    {
        SupervisorBoundsRecitation.Render(0, 8, 5, maxTotalSpawns: null)!
            .ShouldContain($"5 of {SupervisorLane.DefaultMaxTotalSpawns} total-spawn cap", Case.Sensitive, "readers fall back to the lane default — the recitation mirrors them");
    }

    [Fact]
    public void The_header_is_pinned()
    {
        // A stable prompt landmark, mirroring SupervisorBudgetRecitation.Header — tests and the model key on it.
        SupervisorBoundsRecitation.Header.ShouldBe("RUN BOUNDS (recite before deciding — hitting a bound force-stops the run):");
    }

    // ── The prompt wiring ───────────────────────────────────────────────────────────

    [Fact]
    public void The_user_prompt_carries_the_bounds_block_once_counters_move()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(new SupervisorTurnContext
        {
            Goal = "ship it", TurnNumber = 5, PriorDecisions = Array.Empty<SupervisorPriorDecision>(),
            NoProgressDecisions = 6, MaxNoProgressDecisions = 8, TotalSpawnedAgents = 5, MaxTotalSpawns = 50,
        });

        prompt.ShouldContain("RUN BOUNDS", Case.Sensitive);
        prompt.ShouldContain("no-progress decisions: 6 of 8", Case.Sensitive);
        prompt.ShouldContain("agents spawned: 5 of 50", Case.Sensitive);
    }

    [Fact]
    public void A_fresh_runs_prompt_has_no_bounds_block()
    {
        LlmSupervisorDecider.BuildUserPromptForTest(new SupervisorTurnContext { Goal = "ship it", TurnNumber = 0, PriorDecisions = Array.Empty<SupervisorPriorDecision>() })
            .ShouldNotContain("RUN BOUNDS", Case.Sensitive, "byte-identical prompt while nothing is at risk");
    }
}
