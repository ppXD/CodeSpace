using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the resolver loop (#379, S2) PURE surface — the deterministic recipe, the <c>resolve</c> verb's
/// projection + schema membership + governance classification, and the dedicated resolve-attempt bound. These pin
/// the model-free half of fork #2: the decider only CHOOSES <c>resolve</c>; the recipe + bounds are deterministic.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorResolverTests
{
    private static SupervisorIntegrationOutcome Conflict(params string[] files) => new()
    {
        Status = "Conflicted",
        ConflictedFiles = files,
        PreservedBranches = new[] { "codespace/agent/b" },
        Reason = "a contribution conflicted while integrating",
    };

    // ── The deterministic resolver recipe ────────────────────────────────────────────

    [Fact]
    public void The_recipe_names_the_goal_every_branch_the_conflicted_files_and_the_gates()
    {
        var instruction = SupervisorResolverRecipe.BuildInstruction(
            "ship the feature",
            Conflict("src/Foo.cs", "src/Bar.cs"),
            new[] { "codespace/agent/web", "codespace/agent/api" });

        instruction.ShouldContain("ship the feature", Case.Insensitive, "the resolver sees the overarching goal");
        instruction.ShouldContain("codespace/agent/web");
        instruction.ShouldContain("codespace/agent/api", customMessage: "EVERY branch to reconcile is named (the full set, not just the conflicting one)");
        instruction.ShouldContain("src/Foo.cs");
        instruction.ShouldContain("src/Bar.cs", customMessage: "the conflicted files are called out");
        instruction.ShouldContain("merge", Case.Insensitive, "the branch-pair re-merge is spelled out");
        instruction.ShouldContain("test", Case.Insensitive, "the build/test gate is instructed");
        instruction.ShouldContain("only if", Case.Insensitive, "commit is gated on green");
        instruction.ShouldContain("do not invent", Case.Insensitive, "the reconcile-don't-invent guardrail is present");
        instruction.ShouldContain(SupervisorResolverRecipe.TestsPassedMarker, customMessage: "the instruction-encoded verdict marker S3 reads is embedded");
    }

    [Fact]
    public void The_recipe_is_deterministic_in_its_inputs()
    {
        var a = SupervisorResolverRecipe.BuildInstruction("g", Conflict("f.cs"), new[] { "b1", "b2" });
        var b = SupervisorResolverRecipe.BuildInstruction("g", Conflict("f.cs"), new[] { "b1", "b2" });

        a.ShouldBe(b, "same inputs → byte-identical instruction (a replay re-derives the same resolver task)");
    }

    [Fact]
    public void The_verified_marker_is_pinned()
    {
        // Load-bearing: S3 reads this exact token off the resolver's summary as the verification verdict. A rename
        // must be a visible decision, not a silent drift that makes every resolution read as unverified.
        SupervisorResolverRecipe.TestsPassedMarker.ShouldBe("RESOLUTION_VERIFIED");
    }

    // ── The resolve verb: projection + schema + governance ─────────────────────────────

    [Fact]
    public void Resolve_projects_to_a_non_terminal_canonical_decision()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Resolve,
            Resolve = new SupervisorResolvePayload { Note = "the integration conflicted" },
        });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Resolve);
        decision.IsTerminal.ShouldBeFalse("resolve spawns a resolver agent; the loop continues");
    }

    [Fact]
    public void Resolve_with_a_missing_payload_projects_to_a_safe_empty_payload()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Resolve });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Resolve, "a model that picked resolve but sent no sub-object still resolves cleanly (the executor derives everything)");
    }

    [Fact]
    public void The_decision_schema_offers_resolve_as_a_verb()
    {
        var kinds = SupervisorDecisionSchema.ResponseSchema
            .GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        kinds.ShouldContain("resolve", "the decider may pick resolve after a conflicted merge");
    }

    [Fact]
    public void Resolve_is_side_effecting_so_it_is_governed_like_a_spawn()
    {
        SupervisorGovernance.IsSideEffecting(SupervisorDecisionKinds.Resolve).ShouldBeTrue("resolve stages a real agent run — it must route through the governance gate");
    }

    // ── The dedicated resolve-attempt bound ────────────────────────────────────────────

    private static SupervisorTurnContext ContextWithResolves(int priorResolves)
    {
        var prior = Enumerable.Range(0, priorResolves)
            .Select(i => new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = i + 1, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = "{}" })
            .ToArray();

        return new SupervisorTurnContext { Goal = "g", SupervisorRunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), NodeId = "sup", TurnNumber = priorResolves + 1, PriorDecisions = prior };
    }

    private static SupervisorDecision ResolveDecision() => new() { Kind = SupervisorDecisionKinds.Resolve, PayloadJson = "{}" };

    [Fact]
    public void The_first_resolve_is_allowed_under_the_default_cap_of_one()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig());   // default MaxResolveAttempts = 1

        SupervisorBounds.PostDecision(ContextWithResolves(0), plan, ResolveDecision())
            .ShouldBeNull("the first resolve attempt proceeds (no prior resolve on the tape)");
    }

    [Fact]
    public void A_second_resolve_force_stops_at_the_default_cap()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig());

        SupervisorBounds.PostDecision(ContextWithResolves(1), plan, ResolveDecision())
            .ShouldBe(SupervisorStopReasons.ResolveAttemptsExceeded, "with the cap at 1, a second resolve falls back fail-safe to the humans");
    }

    [Fact]
    public void An_operator_may_raise_the_resolve_cap_within_the_ceiling()
    {
        var plan = SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxResolveAttempts = 3 });

        SupervisorBounds.PostDecision(ContextWithResolves(2), plan, ResolveDecision()).ShouldBeNull("2 prior resolves < cap 3 → allowed");
        SupervisorBounds.PostDecision(ContextWithResolves(3), plan, ResolveDecision()).ShouldBe(SupervisorStopReasons.ResolveAttemptsExceeded, "3 prior resolves == cap 3 → refused");
    }

    [Fact]
    public void The_resolve_cap_is_clamped_to_the_ceiling()
    {
        SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxResolveAttempts = 999 }).MaxResolveAttempts
            .ShouldBe(SupervisorLane.MaxResolveAttemptsCeiling, "a fat-fingered config can't disable the bound");

        SupervisorGoalPlan.From(new SupervisorGoalConfig { MaxResolveAttempts = 0 }).MaxResolveAttempts
            .ShouldBe(SupervisorLane.DefaultMaxResolveAttempts, "a zero/negative cap falls back to the safe default");
    }
}
