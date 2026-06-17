using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
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

    [Theory]
    // The single classifier the rehydrate folds (spend / total / progress / agent-results), the phase projector, the
    // scorecard, and the decider's agent-result rendering all share — so the resolver agent's spend counts toward the
    // cost cap, its run appears on the Agent-Board, and its result reaches the decider. A drift here is a real bug.
    [InlineData(SupervisorDecisionKinds.Spawn, true)]
    [InlineData(SupervisorDecisionKinds.Retry, true)]
    [InlineData(SupervisorDecisionKinds.Resolve, true)]
    [InlineData(SupervisorDecisionKinds.Plan, false)]
    [InlineData(SupervisorDecisionKinds.Merge, false)]
    [InlineData(SupervisorDecisionKinds.AskHuman, false)]
    [InlineData(SupervisorDecisionKinds.Stop, false)]
    public void StagesAgents_is_true_exactly_for_the_agent_staging_verbs(string kind, bool expected)
    {
        SupervisorDecisionKinds.StagesAgents(kind).ShouldBe(expected);
    }

    // ── S3: the build/test verification verdict ────────────────────────────────────

    /// <summary>A resolve outcome whose folded resolver agent terminated with the given status + summary (the shape FoldAgentResults persists for a resolve decision after the barrier).</summary>
    private static string ResolveOutcome(string status, string? summary)
    {
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = status, Summary = summary };
        return System.Text.Json.JsonSerializer.Serialize(new { agentRunIds = new[] { result.AgentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);
    }

    [Fact]
    public void ReadResolutionVerdict_is_Verified_only_when_the_resolver_succeeded_with_the_marker()
    {
        SupervisorOutcome.ReadResolutionVerdict(ResolveOutcome("Succeeded", $"reconciled cleanly. {SupervisorResolverRecipe.TestsPassedMarker}"))
            .ShouldBe(SupervisorResolutionVerdict.Verified);
    }

    [Theory]
    [InlineData("Succeeded", "reconciled but tests still failing")]   // succeeded, NO marker → unverified
    [InlineData("Failed", "build broke")]                            // didn't even succeed
    [InlineData("Cancelled", null)]                                  // killed mid-run
    public void ReadResolutionVerdict_is_Unverified_without_a_verified_pass(string status, string? summary)
    {
        SupervisorOutcome.ReadResolutionVerdict(ResolveOutcome(status, summary))
            .ShouldBe(SupervisorResolutionVerdict.Unverified, "no green-tests marker on a terminal resolver ⇒ NOT safe to accept");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"agentRunIds":["x"],"agentCount":1}""")]   // staged but not yet folded (still parked)
    public void ReadResolutionVerdict_is_Unknown_when_no_resolver_result_is_folded(string? outcomeJson)
    {
        SupervisorOutcome.ReadResolutionVerdict(outcomeJson).ShouldBe(SupervisorResolutionVerdict.Unknown);
    }

    [Fact]
    public void The_decider_prompt_renders_a_verified_resolution_as_safe_to_accept()
    {
        var resolve = new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = ResolveOutcome("Succeeded", $"done {SupervisorResolverRecipe.TestsPassedMarker}") };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4, resolve));

        prompt.ShouldContain("resolution VERIFIED", Case.Insensitive);
        prompt.ShouldContain("safe to accept", Case.Insensitive);
    }

    [Fact]
    public void The_decider_prompt_renders_an_unverified_resolution_as_do_not_accept()
    {
        var resolve = new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = ResolveOutcome("Succeeded", "tests still red") };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 4, resolve));

        prompt.ShouldContain("NOT verified", Case.Insensitive);
        prompt.ShouldContain("do NOT accept", Case.Insensitive);
    }

    private static SupervisorTurnContext Context(int turnNumber, params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship", TurnNumber = turnNumber, PriorDecisions = prior };

    [Fact]
    public void The_eval_scorecard_counts_resolve_decisions()
    {
        var score = SupervisorEvalScorecard.Score(new SupervisorRunOutcome
        {
            SupervisorRunId = Guid.NewGuid(),
            Decisions = new[]
            {
                new SupervisorDecisionSummary { Kind = SupervisorDecisionKinds.Plan, StagedAgentCount = 0 },
                new SupervisorDecisionSummary { Kind = SupervisorDecisionKinds.Resolve, StagedAgentCount = 1 },
                new SupervisorDecisionSummary { Kind = SupervisorDecisionKinds.Stop, StagedAgentCount = 0, StopReason = "done" },
            },
            SpawnedAgentStatuses = Array.Empty<AgentRunStatus>(),
            TerminalStatus = WorkflowRunStatus.Success,
        });

        score.ResolveCount.ShouldBe(1, "a resolve attempt is now a first-class per-verb metric on the scorecard");
    }

    // ── S4: the irreversible HITL acceptance gate (the safety floor) ───────────────────

    [Theory]
    [InlineData(SupervisorDecisionKinds.Resolve, true)]
    [InlineData(SupervisorDecisionKinds.Spawn, false)]
    [InlineData(SupervisorDecisionKinds.Retry, false)]
    [InlineData(SupervisorDecisionKinds.Merge, false)]
    [InlineData(SupervisorDecisionKinds.Stop, false)]
    public void Only_resolve_is_irreversible(string kind, bool expected)
    {
        SupervisorGovernance.IsIrreversible(kind).ShouldBe(expected, "a resolve autonomously re-merges code — it ALWAYS needs a human; the other verbs don't");
    }

    [Fact]
    public void A_resolve_requires_human_approval_even_under_the_autonomous_policy()
    {
        // The safety floor: under None (autonomous) a spawn runs without approval, but a resolve ESCALATES to
        // RequireApproval — a model never autonomously re-merges code without a human OK.
        SupervisorGovernance.Decide(SupervisorDecisionKinds.Spawn, SupervisorApprovalPolicy.None, irreversible: SupervisorGovernance.IsIrreversible(SupervisorDecisionKinds.Spawn))
            .ShouldBe(AgentToolGateDecision.Allow, "a normal spawn under the autonomous policy still runs autonomously");

        SupervisorGovernance.Decide(SupervisorDecisionKinds.Resolve, SupervisorApprovalPolicy.None, irreversible: SupervisorGovernance.IsIrreversible(SupervisorDecisionKinds.Resolve))
            .ShouldBe(AgentToolGateDecision.RequireApproval, "a resolve under the SAME autonomous policy escalates to a human approval — the irreversible floor");
    }

    [Fact]
    public void A_resolve_still_requires_approval_under_the_approve_spawns_policy()
    {
        SupervisorGovernance.Decide(SupervisorDecisionKinds.Resolve, SupervisorApprovalPolicy.Spawns, irreversible: true)
            .ShouldBe(AgentToolGateDecision.RequireApproval);
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
