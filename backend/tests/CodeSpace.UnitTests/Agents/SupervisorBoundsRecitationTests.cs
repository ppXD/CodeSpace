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

    // ── P5-5: the resolver runway ───────────────────────────────────────────────────

    [Fact]
    public void The_resolve_line_names_the_count_the_cap_and_the_human_fallback()
    {
        var block = SupervisorBoundsRecitation.Render(0, 8, 0, 50, resolveAttempts: 1, maxResolveAttempts: 2);

        block.ShouldNotBeNull("a spent resolve attempt alone warrants the block — the next one past the cap kills the run");
        block!.ShouldContain("resolve attempts: 1 of 2 resolve cap", Case.Sensitive);
        block.ShouldContain("a resolve past the cap force-stops this run", Case.Sensitive, "unlike a spawn wave, an over-cap resolve is not refused — it is the run's death; the model must know which");
        block.ShouldContain("stop and leave the conflict to a human", Case.Sensitive, "the fail-safe exit is named, mirroring the resolution-verdict copy");
        block.ShouldNotContain("no-progress decisions", Case.Sensitive);
        block.ShouldNotContain("agents spawned", Case.Sensitive);
    }

    [Fact]
    public void A_legacy_context_without_a_resolve_cap_falls_back_to_the_lane_default()
    {
        SupervisorBoundsRecitation.Render(0, 8, 0, 50, resolveAttempts: 1, maxResolveAttempts: null)!
            .ShouldContain($"resolve attempts: 1 of {SupervisorLane.DefaultMaxResolveAttempts} resolve cap", Case.Sensitive);
    }

    [Fact]
    public void Zero_resolve_attempts_render_no_resolve_line()
    {
        SupervisorBoundsRecitation.Render(6, 8, 0, 50)!
            .ShouldNotContain("resolve attempts", Case.Sensitive, "no resolve on the tape → no resolver line, the default params keep every P5-3 caller byte-identical");
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

    [Fact]
    public void The_user_prompt_counts_resolves_off_the_tape_exactly_like_the_bound_does()
    {
        // The prompt's count mirrors SupervisorBounds.PostDecision's own tape count (prior Resolve decisions) —
        // producer and recitation can't drift because both read the same rows the same way.
        var resolve = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}", OutcomeJson = """{"agentRunIds":[],"agentCount":0}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(new SupervisorTurnContext
        {
            Goal = "ship it", TurnNumber = 4, PriorDecisions = new[] { resolve }, MaxResolveAttempts = 2,
        });

        prompt.ShouldContain("resolve attempts: 1 of 2 resolve cap", Case.Sensitive);
    }
}
