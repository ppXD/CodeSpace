using System.Text.Json;
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

    // ── S5: the run's final integrated branch (the open_pr output surface) ──────────

    /// <summary>A resolve outcome whose folded resolver agent pushed <paramref name="producedBranch"/> (S5 reads ProducedBranch off the first folded result) and ended with the given status/summary.</summary>
    private static string ResolveOutcomeWithBranch(string status, string? summary, string? producedBranch)
    {
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = status, Summary = summary, ProducedBranch = producedBranch };
        return JsonSerializer.Serialize(new { agentRunIds = new[] { result.AgentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);
    }

    /// <summary>A merge outcome carrying an <c>integration</c> block of the given status + (clean) branch — the shape <c>ProjectIntegrationResult</c> records.</summary>
    private static string MergeOutcome(string integrationStatus, string? integratedBranch) =>
        JsonSerializer.Serialize(new { merged = Array.Empty<object>(), count = 0, integration = new { status = integrationStatus, integratedBranch } }, AgentJson.Options);

    private static SupervisorPriorDecision Decision(string kind, long seq, string? outcomeJson) =>
        new() { Id = Guid.NewGuid(), Sequence = seq, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson };

    private static readonly string VerifiedSummary = $"reconciled cleanly. {SupervisorResolverRecipe.TestsPassedMarker}";

    [Fact]
    public void ReadFinalIntegratedBranch_is_null_on_an_empty_tape() =>
        SupervisorOutcome.ReadFinalIntegratedBranch(Array.Empty<SupervisorPriorDecision>()).ShouldBeNull();

    [Fact]
    public void ReadFinalIntegratedBranch_surfaces_a_clean_merge_branch() =>
        SupervisorOutcome.ReadFinalIntegratedBranch(new[] { Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Clean", "codespace/integration/x")) })
            .ShouldBe("codespace/integration/x");

    [Theory]
    [InlineData("Conflicted", null)]   // the K branches couldn't auto-combine → no branch
    [InlineData("Skipped", null)]      // nothing to integrate
    [InlineData("Failed", null)]       // a git infrastructure error
    [InlineData("Clean", "")]          // clean but an empty branch string → never surfaced
    public void ReadFinalIntegratedBranch_ignores_a_merge_without_a_clean_branch(string status, string? branch) =>
        SupervisorOutcome.ReadFinalIntegratedBranch(new[] { Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome(status, branch)) })
            .ShouldBeNull();

    [Fact]
    public void ReadFinalIntegratedBranch_surfaces_a_verified_resolvers_own_branch()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Spawn, 1, "{}"),
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", null)),
            Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r")),
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBe("codespace/resolve/r", "a VERIFIED resolver's own tested branch IS the reconciled merge");
    }

    [Fact]
    public void ReadFinalIntegratedBranch_ignores_an_unverified_resolution() =>
        SupervisorOutcome.ReadFinalIntegratedBranch(new[] { Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("Succeeded", "tests still red", "codespace/resolve/r")) })
            .ShouldBeNull("a resolution that didn't pass tests must NEVER surface a branch a downstream open_pr would ship");

    [Fact]
    public void ReadFinalIntegratedBranch_ignores_a_verified_resolution_that_pushed_no_branch() =>
        SupervisorOutcome.ReadFinalIntegratedBranch(new[] { Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, null)) })
            .ShouldBeNull();

    [Fact]
    public void ReadFinalIntegratedBranch_takes_the_latest_when_a_clean_merge_follows_a_resolution()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r")),
            Decision(SupervisorDecisionKinds.Merge, 4, MergeOutcome("Clean", "codespace/integration/later")),
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBe("codespace/integration/later", "the LATEST integration wins (a fresh wave merged after the resolution)");
    }

    [Fact]
    public void ReadFinalIntegratedBranch_skips_a_reconflicted_merge_after_a_verified_resolution()
    {
        // Defensive: even if a merge AFTER a verified resolution re-conflicted (Part B should prevent this, but the
        // reader must be robust regardless), the reverse walk skips the no-branch conflicted merge and surfaces the
        // verified resolver branch — a verified resolution is never buried by a later failed integration.
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r")),
            Decision(SupervisorDecisionKinds.Merge, 4, MergeOutcome("Conflicted", null)),
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBe("codespace/resolve/r");
    }

    [Fact]
    public void ReadFinalIntegratedBranch_takes_the_latest_of_multiple_clean_merges()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Clean", "codespace/integration/wave1")),
            Decision(SupervisorDecisionKinds.Merge, 4, MergeOutcome("Clean", "codespace/integration/wave2")),
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBe("codespace/integration/wave2");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"integration":"oops"}""")]   // integration present but not an object
    public void ReadFinalIntegratedBranch_tolerates_a_malformed_merge_outcome(string outcomeJson) =>
        SupervisorOutcome.ReadFinalIntegratedBranch(new[] { Decision(SupervisorDecisionKinds.Merge, 2, outcomeJson) })
            .ShouldBeNull();

    [Fact]
    public void ReadFinalIntegratedBranch_ignores_plan_spawn_and_stop_decisions()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Plan, 1, "{}"),
            Decision(SupervisorDecisionKinds.Spawn, 2, "{}"),
            Decision(SupervisorDecisionKinds.Stop, 3, "{}"),
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBeNull("only a clean merge or a verified resolve is a reviewable integrated branch");
    }

    // S5 review fold: a spawn/retry that NOTHING later merged/resolved is a barrier — the run's latest work is
    // un-combined, so an earlier branch must NOT be surfaced past it (this is what keeps Part A consistent with Part B).
    [Fact]
    public void ReadFinalIntegratedBranch_returns_null_when_a_spawn_follows_a_verified_resolution_unmerged()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r")),
            Decision(SupervisorDecisionKinds.Spawn, 4, "{}"),   // a fresh wave staged AFTER the resolution, never merged
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBeNull("the resolver branch is STALE once a new wave is spawned — surfacing it would ship a PR missing that wave (matches Part B's disqualifier)");
    }

    [Fact]
    public void ReadFinalIntegratedBranch_returns_null_when_a_spawn_follows_a_clean_merge_unmerged()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Clean", "codespace/integration/wave1")),
            Decision(SupervisorDecisionKinds.Spawn, 3, "{}"),   // wave2 staged after wave1 integrated, never re-merged
        };

        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBeNull("wave1's clean branch is stale once wave2 is spawned un-integrated");
    }

    // ── PR-5 I3: FindUnpublishedFrontier — mirrors ReadFinalIntegratedBranch's EXACT walk, by construction ──

    [Fact]
    public void FindUnpublishedFrontier_is_null_on_an_empty_tape() =>
        SupervisorOutcome.FindUnpublishedFrontier(Array.Empty<SupervisorPriorDecision>()).ShouldBeNull("nothing was ever produced");

    [Fact]
    public void FindUnpublishedFrontier_is_null_when_the_latest_merge_is_clean()
    {
        var tape = new[] { Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Clean", "codespace/integration/x")) };

        SupervisorOutcome.FindUnpublishedFrontier(tape).ShouldBeNull("published — the SAME barrier ReadFinalIntegratedBranch already crosses");
    }

    [Fact]
    public void FindUnpublishedFrontier_is_null_when_the_latest_resolve_is_verified()
    {
        var tape = new[] { Decision(SupervisorDecisionKinds.Resolve, 1, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r")) };

        SupervisorOutcome.FindUnpublishedFrontier(tape).ShouldBeNull("a verified resolution IS a publish");
    }

    [Fact]
    public void FindUnpublishedFrontier_names_a_spawn_that_nothing_later_merged()
    {
        var spawn = Decision(SupervisorDecisionKinds.Spawn, 1, "{}");

        SupervisorOutcome.FindUnpublishedFrontier(new[] { spawn }).ShouldBe(spawn);
    }

    [Fact]
    public void FindUnpublishedFrontier_names_the_frontier_underneath_a_conflicted_merge_not_the_merge_itself()
    {
        // A non-clean merge is TRANSPARENT to this walk (mirrors ReadFinalIntegratedBranch): it matches neither the
        // clean-branch check nor the StagesAgents check, so the walk continues past it to the real frontier — the
        // caller (SupervisorPublishGate) separately detects "a merge already ran after this frontier" by sequence.
        var spawn = Decision(SupervisorDecisionKinds.Spawn, 1, "{}");
        var tape = new[] { spawn, Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", null)) };

        SupervisorOutcome.FindUnpublishedFrontier(tape).ShouldBe(spawn);
    }

    [Fact]
    public void FindUnpublishedFrontier_names_the_latest_staging_decision_when_several_exist()
    {
        var latest = Decision(SupervisorDecisionKinds.Retry, 3, "{}");
        var tape = new[] { Decision(SupervisorDecisionKinds.Spawn, 1, "{}"), Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", null)), latest };

        SupervisorOutcome.FindUnpublishedFrontier(tape).ShouldBe(latest, "the MOST RECENT unconsolidated staging decision is the frontier, not an earlier one");
    }

    // ── The SHARED verified-resolution-branch predicate (Part A + Part B call this one helper — Rule 7) ──

    [Fact]
    public void ResolvedBranch_returns_the_branch_only_for_a_verified_resolve_that_pushed()
    {
        SupervisorOutcome.ResolvedBranch(Decision(SupervisorDecisionKinds.Resolve, 1, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r")))
            .ShouldBe("codespace/resolve/r");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.Merge)]
    public void ResolvedBranch_is_null_for_a_non_resolve_decision(string kind) =>
        SupervisorOutcome.ResolvedBranch(Decision(kind, 1, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r"))).ShouldBeNull();

    [Fact]
    public void ResolvedBranch_is_null_for_an_unverified_resolve() =>
        SupervisorOutcome.ResolvedBranch(Decision(SupervisorDecisionKinds.Resolve, 1, ResolveOutcomeWithBranch("Succeeded", "tests red", "codespace/resolve/r"))).ShouldBeNull();

    [Fact]
    public void ResolvedBranch_is_null_for_a_verified_resolve_that_pushed_no_branch() =>
        SupervisorOutcome.ResolvedBranch(Decision(SupervisorDecisionKinds.Resolve, 1, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, null))).ShouldBeNull();

    // ── S7-D1: per-repo node output (ReadFinalRepositoryBranches) — the MULTI-repo complement of ReadFinalIntegratedBranch ──

    /// <summary>A MULTI-repo merge outcome: the aggregate status + a per-repo <c>repositories[]</c> array, each block its own status/integratedBranch.</summary>
    private static string MultiRepoMergeOutcome(string aggregateStatus, params (string Alias, Guid? RepoId, string Status, string? Branch)[] repos) =>
        JsonSerializer.Serialize(new
        {
            merged = Array.Empty<object>(), count = 0,
            integration = new
            {
                status = aggregateStatus,
                repositories = repos.Select(r => new { repositoryId = r.RepoId, alias = r.Alias, status = r.Status, integratedBranch = r.Branch, baseBranch = $"base-{r.Alias}" }).ToArray(),
            },
        }, AgentJson.Options);

    [Fact]
    public void ReadFinalRepositoryBranches_surfaces_each_clean_repos_branch()
    {
        var webId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Spawn, 1, "{}"),
            Decision(SupervisorDecisionKinds.Merge, 2, MultiRepoMergeOutcome("Clean",
                ("web", webId, "Clean", "codespace/integration/run/turn1"),
                ("api", apiId, "Clean", "codespace/integration/run/turn1"))),
        };

        var branches = SupervisorOutcome.ReadFinalRepositoryBranches(tape);

        branches.Count.ShouldBe(2);
        branches.Single(b => b.Alias == "web").RepositoryId.ShouldBe(webId, "each per-repo branch names its repo — the per-repo PR-open key");
        var api = branches.Single(b => b.Alias == "api");
        api.SourceBranch.ShouldBe("codespace/integration/run/turn1");
        api.TargetBranch.ShouldBe("base-api", "S7-E: the merge block's baseBranch is surfaced as the PR target so git.open_change_set binds it");
    }

    [Fact]
    public void ReadFinalRepositoryBranches_includes_only_the_CLEAN_repos_of_a_partial_conflict()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Spawn, 1, "{}"),
            Decision(SupervisorDecisionKinds.Merge, 2, MultiRepoMergeOutcome("Conflicted",
                ("web", Guid.NewGuid(), "Clean", "codespace/integration/run/turn1"),
                ("api", Guid.NewGuid(), "Conflicted", null))),
        };

        var branches = SupervisorOutcome.ReadFinalRepositoryBranches(tape);

        branches.Select(b => b.Alias).ShouldBe(new[] { "web" }, "only the cleanly-integrated repo surfaces a branch; the conflicted repo has none (its resolution is S7-D2)");
    }

    [Fact]
    public void ReadFinalRepositoryBranches_is_empty_for_a_single_repo_merge()
    {
        // A single-repo run uses the flat integratedBranch (ReadFinalIntegratedBranch); its block has no repositories[].
        var tape = new[] { Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Clean", "codespace/integration/x")) };

        SupervisorOutcome.ReadFinalRepositoryBranches(tape).ShouldBeEmpty("a single-repo merge has no per-repo repositories[] — it surfaces the single integratedBranch instead");
        SupervisorOutcome.ReadFinalIntegratedBranch(tape).ShouldBe("codespace/integration/x", "...and the single-repo reader still surfaces the one branch (the two are complementary)");
    }

    [Fact]
    public void ReadFinalRepositoryBranches_returns_empty_when_a_spawn_follows_the_merge_unmerged()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Merge, 2, MultiRepoMergeOutcome("Clean", ("web", Guid.NewGuid(), "Clean", "codespace/integration/run/turn1"))),
            Decision(SupervisorDecisionKinds.Spawn, 3, "{}"),   // a fresh wave staged after the merge, never re-merged
        };

        SupervisorOutcome.ReadFinalRepositoryBranches(tape).ShouldBeEmpty("the per-repo set is STALE once a new wave is spawned un-integrated — the same barrier as ReadFinalIntegratedBranch");
    }

    [Fact]
    public void ReadFinalRepositoryBranches_takes_the_latest_multi_repo_merge()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Merge, 2, MultiRepoMergeOutcome("Clean", ("web", Guid.NewGuid(), "Clean", "codespace/integration/run/turn1"))),
            Decision(SupervisorDecisionKinds.Merge, 4, MultiRepoMergeOutcome("Clean",
                ("web", Guid.NewGuid(), "Clean", "codespace/integration/run/turn3"),
                ("api", Guid.NewGuid(), "Clean", "codespace/integration/run/turn3"))),
        };

        var branches = SupervisorOutcome.ReadFinalRepositoryBranches(tape);

        branches.Count.ShouldBe(2, "the latest merge wins");
        branches.ShouldAllBe(b => b.SourceBranch == "codespace/integration/run/turn3");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"integration":"oops"}""")]                              // integration present but not an object
    [InlineData("""{"integration":{"status":"Clean","repositories":"oops"}}""")]   // repositories present but not an array
    [InlineData("""{"integration":{"status":"Clean","repositories":[{"status":"Clean"}]}}""")]  // clean repo but NO integratedBranch
    public void ReadFinalRepositoryBranches_tolerates_a_malformed_or_branchless_block(string outcomeJson) =>
        SupervisorOutcome.ReadFinalRepositoryBranches(new[] { Decision(SupervisorDecisionKinds.Merge, 2, outcomeJson) })
            .ShouldBeEmpty("a malformed / branchless multi-repo block degrades to empty, never a crash");

    [Fact]
    public void ReadFinalRepositoryBranches_tolerates_a_null_repository_id_on_a_clean_block()
    {
        // A degraded clean block whose repositoryId is null/non-Guid still surfaces its branch (alias + branch are
        // present) with RepositoryId null — the reader never throws on the Guid.TryParse round-trip.
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Merge, 2, """{"integration":{"status":"Clean","repositories":[{"repositoryId":null,"alias":"orphan","status":"Clean","integratedBranch":"codespace/integration/run/turn1"}]}}"""),
        };

        var branch = SupervisorOutcome.ReadFinalRepositoryBranches(tape).ShouldHaveSingleItem();
        branch.RepositoryId.ShouldBeNull();
        branch.Alias.ShouldBe("orphan");
        branch.SourceBranch.ShouldBe("codespace/integration/run/turn1");
    }

    // ── S7-D2: per-repo RESOLUTION (one multi-repo resolver; per-repo accept) ──────────

    /// <summary>A resolve outcome whose single resolver agent is MULTI-repo — its RepositoryResults carry each repo's reconciled, pushed branch (the shape the multi-repo capture+offload path persists).</summary>
    private static string ResolveOutcomeWithRepos(string status, string summary, params (string Alias, Guid? RepoId, string? Branch)[] repos)
    {
        var result = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = status, Summary = summary, ProducedBranch = repos.FirstOrDefault().Branch,
            // Each per-repo entry carries its base (the ref the resolver reconciled onto) so the resolve-only node-output
            // path (ReadFinalRepositoryBranches → ResolvedRepositoryBranches) surfaces TargetBranch for git.open_change_set.
            RepositoryResults = repos.Select(r => new RepositoryRunResult { Alias = r.Alias, RepositoryId = r.RepoId, ProducedBranch = r.Branch, BaseBranch = $"base-{r.Alias}", Access = WorkspaceAccess.Write }).ToArray(),
        };
        return JsonSerializer.Serialize(new { agentRunIds = new[] { result.AgentRunId }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);
    }

    /// <summary>A MULTI-repo conflicted merge: per-repo blocks, each with its own status + (for a Conflicted block) outcomes carrying conflicted files.</summary>
    private static string MultiRepoConflictedMerge(params (string Alias, Guid? RepoId, string Status, string[]? Files)[] repos) =>
        JsonSerializer.Serialize(new
        {
            integration = new
            {
                status = "Conflicted",
                repositories = repos.Select(r => new
                {
                    repositoryId = r.RepoId,
                    alias = r.Alias,
                    status = r.Status,
                    outcomes = r.Files is null ? Array.Empty<object>() : new[] { new { label = "agent", disposition = "Conflicted", conflictedFiles = r.Files, fallbackBranch = $"codespace/agent/{r.Alias}" } },
                }).ToArray(),
            },
        }, AgentJson.Options);

    [Fact]
    public void ResolvedRepositoryBranches_surfaces_a_verified_multi_repo_resolvers_per_repo_branches()
    {
        var apiId = Guid.NewGuid();
        var decision = Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithRepos("Succeeded", VerifiedSummary,
            ("web", Guid.NewGuid(), "codespace/resolve/web"), ("api", apiId, "codespace/resolve/api")));

        var branches = SupervisorOutcome.ResolvedRepositoryBranches(decision);

        branches.Count.ShouldBe(2);
        var api = branches.Single(b => b.Alias == "api");
        api.RepositoryId.ShouldBe(apiId);
        api.SourceBranch.ShouldBe("codespace/resolve/api");
        api.TargetBranch.ShouldBe("base-api", "S7-E: a verified resolver's per-repo base flows to TargetBranch so a stop-after-resolve run's PRs have a target");
    }

    [Fact]
    public void ResolvedRepositoryBranches_is_empty_for_an_unverified_or_single_repo_resolve()
    {
        SupervisorOutcome.ResolvedRepositoryBranches(Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithRepos("Succeeded", "tests red", ("web", Guid.NewGuid(), "b"))))
            .ShouldBeEmpty("an unverified resolution surfaces no per-repo branches");

        SupervisorOutcome.ResolvedRepositoryBranches(Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithBranch("Succeeded", VerifiedSummary, "codespace/resolve/r")))
            .ShouldBeEmpty("a single-repo resolver (no RepositoryResults) surfaces via ResolvedBranch, not here");
    }

    [Fact]
    public void ResolvedBranch_is_null_for_a_MULTI_repo_resolver()
    {
        // A multi-repo resolver's top-level ProducedBranch mirrors only the primary — surfacing it as THE single branch
        // would drop the other repos, so ResolvedBranch returns null (the per-repo branches go through ResolvedRepositoryBranches).
        SupervisorOutcome.ResolvedBranch(Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithRepos("Succeeded", VerifiedSummary, ("web", Guid.NewGuid(), "codespace/resolve/web"))))
            .ShouldBeNull();
    }

    [Fact]
    public void ReadFinalRepositoryBranches_surfaces_a_verified_multi_repo_resolution_on_stop_after_resolve()
    {
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Spawn, 1, "{}"),
            Decision(SupervisorDecisionKinds.Merge, 2, MultiRepoConflictedMerge(("api", Guid.NewGuid(), "Conflicted", new[] { "api/Svc.cs" }))),
            Decision(SupervisorDecisionKinds.Resolve, 3, ResolveOutcomeWithRepos("Succeeded", VerifiedSummary, ("web", Guid.NewGuid(), "codespace/resolve/web"), ("api", Guid.NewGuid(), "codespace/resolve/api"))),
        };

        var branches = SupervisorOutcome.ReadFinalRepositoryBranches(tape);

        branches.Select(b => b.Alias).ShouldBe(new[] { "web", "api" }, ignoreOrder: true, "a stop right after a verified multi-repo resolve surfaces the resolver's per-repo reconciled branches");
        var api = branches.Single(b => b.Alias == "api");
        api.SourceBranch.ShouldBe("codespace/resolve/api", "the resolver's reconciled head is the PR source");
        api.TargetBranch.ShouldBe("base-api", "S7-E: the full stop-after-resolve node-output path carries the PR base too — git.open_change_set binds it");
    }

    [Fact]
    public void ReadConflictedRepos_reads_only_the_conflicted_repos_of_a_multi_repo_merge()
    {
        var apiId = Guid.NewGuid();
        var tape = new[]
        {
            Decision(SupervisorDecisionKinds.Merge, 2, MultiRepoConflictedMerge(
                ("web", Guid.NewGuid(), "Clean", null),
                ("api", apiId, "Conflicted", new[] { "api/Svc.cs", "api/Dto.cs" }))),
        };

        var conflicted = SupervisorOutcome.ReadConflictedRepos(tape);

        conflicted.Count.ShouldBe(1, "only the Conflicted repos are surfaced for resolution — the clean repo is already integrated");
        conflicted[0].RepositoryId.ShouldBe(apiId);
        conflicted[0].Alias.ShouldBe("api");
        conflicted[0].ConflictedFiles.ShouldBe(new[] { "api/Svc.cs", "api/Dto.cs" });
    }

    [Fact]
    public void ReadConflictedRepos_is_empty_for_a_single_repo_conflicted_merge() =>
        SupervisorOutcome.ReadConflictedRepos(new[] { Decision(SupervisorDecisionKinds.Merge, 2, MergeOutcome("Conflicted", null)) })
            .ShouldBeEmpty("a single-repo conflicted merge (no repositories[]) yields no per-repo conflicted blocks — the flat resolve path handles it");

    [Fact]
    public void BuildMultiRepoInstruction_names_each_repos_subdirectory_branches_files_and_the_verified_marker()
    {
        var instruction = SupervisorResolverRecipe.BuildMultiRepoInstruction("ship the coordinated feature", new[]
        {
            new ResolverRepoSection { Alias = "web", Branches = new[] { "codespace/agent/web-a", "codespace/agent/web-b" }, ConflictedFiles = new[] { "web/App.tsx" } },
            new ResolverRepoSection { Alias = "api", Branches = new[] { "codespace/agent/api-a" } },
        });

        instruction.ShouldContain("./web");
        instruction.ShouldContain("./api", customMessage: "each conflicted repo's subdirectory is named so the resolver reconciles it in place");
        instruction.ShouldContain("codespace/agent/web-a");
        instruction.ShouldContain("codespace/agent/web-b");
        instruction.ShouldContain("codespace/agent/api-a", customMessage: "every per-repo branch to reconcile is named — assembled from the spawn's agentResults, not the model");
        instruction.ShouldContain("web/App.tsx", customMessage: "the conflicted files are named per repo");
        instruction.ShouldContain(SupervisorResolverRecipe.TestsPassedMarker);
        instruction.ShouldContain("ship the coordinated feature", Case.Insensitive);
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
