using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="AgentNodeMapping.BuildAgentConfig"/>'s optional <c>mode</c> param — the seam PR-B's dynamic
/// fan-out body threads the model-authored <c>{{item.mode}}</c> through. The default (<c>mode = null</c>) MUST emit
/// JSON byte-identical to before the param existed, so the two existing callers (single-agent + plan-map-synth)
/// stay unchanged; a present mode adds exactly the one key <see cref="Core.Services.Workflows.Nodes.Builtin.AgentCodeNode"/> reads.
/// </summary>
[Trait("Category", "Unit")]
public class AgentNodeMappingTests
{
    [Fact]
    public void BuildAgentConfig_omits_mode_when_null()
    {
        // The byte-identical regression pin: the two existing callers pass no third arg → mode defaults to null →
        // the key is omitted entirely, so the emitted agent.code config is unchanged from before the param existed.
        var config = AgentNodeMapping.BuildAgentConfig("Work on {{item}}", new ResolvedAgentProfile { Harness = "codex-cli" });

        config.TryGetProperty("mode", out _).ShouldBeFalse("an absent mode must not emit the key — the existing callers' JSON stays byte-identical");
    }

    [Theory]
    [InlineData("")]      // a blank mode is treated as absent
    [InlineData("   ")]   // whitespace folds to absent (NullIfBlank)
    public void BuildAgentConfig_omits_mode_when_blank(string mode)
    {
        var config = AgentNodeMapping.BuildAgentConfig("g", new ResolvedAgentProfile { Harness = "codex-cli" }, mode);

        config.TryGetProperty("mode", out _).ShouldBeFalse("a blank/whitespace mode folds to absent — the same as null");
    }

    [Theory]
    [InlineData("research")]
    [InlineData("code")]
    [InlineData("{{item.mode}}")]   // the dynamic fan-out body binds the per-branch model-authored mode
    public void BuildAgentConfig_emits_mode_when_present(string mode)
    {
        var config = AgentNodeMapping.BuildAgentConfig("Work on {{item.goal}}", new ResolvedAgentProfile { Harness = "codex-cli" }, mode);

        config.GetProperty("mode").GetString().ShouldBe(mode, "a present mode is emitted as the agent.code config key the node reads");
    }

    // ── displayTitle: the CLEAN pre-grounding goal, so a CONTINUE's card title never shows the digest heading ──

    [Fact]
    public void BuildAgentConfig_emits_displayTitle_as_the_clean_goal_even_when_grounding_is_prepended_to_goal()
    {
        var config = AgentNodeMapping.BuildAgentConfig("Add retry logic", new ResolvedAgentProfile { Harness = "codex-cli" }, grounding: "# Earlier turns in this work thread\nsome digest text");

        config.GetProperty("goal").GetString().ShouldStartWith("# Earlier turns", customMessage: "the MODEL still sees the grounding, prepended as before");
        config.GetProperty("displayTitle").GetString().ShouldBe("Add retry logic", "the CARD title is the clean task text, never the grounding heading");
    }

    [Fact]
    public void BuildAgentConfig_emits_displayTitle_even_with_no_grounding_at_all()
    {
        // A fresh (non-CONTINUE) launch: goal and displayTitle are the SAME text — the field is always present,
        // never special-cased to "only when it would differ from goal".
        var config = AgentNodeMapping.BuildAgentConfig("Add retry logic", new ResolvedAgentProfile { Harness = "codex-cli" });

        config.GetProperty("displayTitle").GetString().ShouldBe("Add retry logic");
    }

    // ── timeoutSeconds: the operator's per-agent wall-clock mapped onto the agent.code config key the node reads. ──

    [Fact]
    public void BuildAgentConfig_omits_timeoutSeconds_when_the_profile_sets_none()
    {
        var config = AgentNodeMapping.BuildAgentConfig("g", new ResolvedAgentProfile { Harness = "codex-cli" });

        config.TryGetProperty("timeoutSeconds", out _).ShouldBeFalse("an absent timeout omits the key — the node applies its bounded 1h default (byte-identical)");
    }

    [Theory]
    [InlineData(7200)]   // a positive value caps the run
    [InlineData(0)]      // an explicit 0 is emitted verbatim — AgentCodeNode maps it to NO wall-clock (the operator's "No limit")
    public void BuildAgentConfig_emits_the_profile_timeoutSeconds_verbatim(int timeout)
    {
        var config = AgentNodeMapping.BuildAgentConfig("g", new ResolvedAgentProfile { Harness = "codex-cli", TimeoutSeconds = timeout });

        config.GetProperty("timeoutSeconds").GetInt32().ShouldBe(timeout, "the per-agent wall-clock is emitted verbatim (incl. 0 = infinite) onto the agent.code config key the node reads");
    }

    // ── reviseRounds: the S6 bounded revise budget mapped onto the agent.code config key the node reads. ──

    [Fact]
    public void BuildAgentConfig_omits_reviseRounds_when_the_profile_sets_none()
    {
        var config = AgentNodeMapping.BuildAgentConfig("g", new ResolvedAgentProfile { Harness = "codex-cli" });

        config.TryGetProperty("reviseRounds", out _).ShouldBeFalse("an absent budget omits the key — the executor's default (1 under Improve, else 0) applies (byte-identical)");
    }

    [Theory]
    [InlineData(0)]   // an explicit 0 is emitted verbatim — the operator turning the loop OFF must survive the mapping
    [InlineData(2)]
    public void BuildAgentConfig_emits_the_profile_reviseRounds_verbatim(int rounds)
    {
        var config = AgentNodeMapping.BuildAgentConfig("g", new ResolvedAgentProfile { Harness = "codex-cli", ReviseRounds = rounds });

        config.GetProperty("reviseRounds").GetInt32().ShouldBe(rounds, "the revise budget is emitted verbatim onto the agent.code config key the node reads");
    }

    // ── BuildAgentInputs: the Rule-16 single home that threads BaseRefs onto baseRef + relatedRepositories[].ref,
    //    shared by single-agent AND plan-map-synth — pinned directly here (both consumers go through this method). ──

    private static readonly Guid Primary = Guid.NewGuid();
    private static readonly Guid Api = Guid.NewGuid();

    private static TaskBuildContext Context(IReadOnlyDictionary<Guid, string>? baseRefs, bool withRelated = true) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "g", SurfaceKind = "chat", TeamId = Guid.NewGuid() },
        Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent },
        AgentProfile = new ResolvedAgentProfile
        {
            RepositoryId = Primary,
            RelatedRepositories = withRelated ? new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Api, Access = WorkspaceAccess.Write } } : null,
        },
        BaseRefs = baseRefs,
    };

    [Fact]
    public void BuildAgentInputs_threads_per_repo_base_refs_onto_baseRef_and_related_ref()
    {
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(new Dictionary<Guid, string> { [Primary] = "run-1/primary", [Api] = "run-1/api" }));

        inputs.GetProperty("baseRef").GetString().ShouldBe("run-1/primary", "the primary's ref comes from the map keyed by its repo id");
        inputs.GetProperty("relatedRepositories")[0].GetProperty("ref").GetString().ShouldBe("run-1/api", "each related repo's ref comes from the map keyed by ITS repo id — no bleed");
        inputs.GetProperty("baseRefFromSession").GetBoolean().ShouldBeTrue("a baseRef from the SESSION map is marked SOFT so a pruned prior branch falls back to the default (Correction-4)");
    }

    [Fact]
    public void BuildAgentInputs_omits_refs_for_repos_absent_from_the_map()
    {
        // The primary has a prior branch; the related repo does NOT → only the primary carries a ref.
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(new Dictionary<Guid, string> { [Primary] = "run-1/primary" }));

        inputs.GetProperty("baseRef").GetString().ShouldBe("run-1/primary");
        inputs.GetProperty("relatedRepositories")[0].TryGetProperty("ref", out _).ShouldBeFalse("a repo absent from the map carries no ref ⇒ it clones at its default branch");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAgentInputs_treats_a_blank_mapped_ref_as_absent(string blank)
    {
        // A blank value in the map (defensive) is folded to absent — the repo clones at its default branch.
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(new Dictionary<Guid, string> { [Primary] = blank, [Api] = blank }));

        inputs.TryGetProperty("baseRef", out _).ShouldBeFalse("a blank mapped ref folds to absent (NullIfBlank) — no baseRef key");
        inputs.GetProperty("relatedRepositories")[0].TryGetProperty("ref", out _).ShouldBeFalse("a blank mapped ref for a related repo emits no ref key");
    }

    [Fact]
    public void BuildAgentInputs_with_no_base_refs_omits_baseRef_and_per_repo_ref_byte_identical()
    {
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(baseRefs: null));

        inputs.TryGetProperty("baseRef", out _).ShouldBeFalse("no map ⇒ no baseRef (default branch, byte-identical to a fresh launch)");
        inputs.GetProperty("relatedRepositories")[0].TryGetProperty("ref", out _).ShouldBeFalse("no map ⇒ no per-repo ref");
        inputs.TryGetProperty("baseRefFromSession", out _).ShouldBeFalse("no baseRef ⇒ no soft marker (an author-set baseRef stays HARD) — byte-identical");
    }

    // ── H3: the operator's fresh-launch BaseBranch was a write-only dead-end (verified: a complete write chain
    //    down to TaskLaunchSeed.BaseBranch, ZERO readers) — an operator pinning a branch got the default silently. ──

    [Fact]
    public void BuildAgentInputs_pins_the_operators_launch_BaseBranch_as_a_HARD_ref()
    {
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(baseRefs: null) with
        {
            Seed = new TaskLaunchSeed { Goal = "g", SurfaceKind = "chat", TeamId = Guid.NewGuid(), BaseBranch = "release/2.x" },
        });

        inputs.GetProperty("baseRef").GetString().ShouldBe("release/2.x", "the operator explicitly pinned the branch — it must reach the clone");
        inputs.TryGetProperty("baseRefFromSession", out _).ShouldBeFalse("an operator pin is a HARD ref: a missing branch must fail LOUD, never silently fall back to the default");
        inputs.GetProperty("relatedRepositories")[0].TryGetProperty("ref", out _).ShouldBeFalse("the pin applies to the PRIMARY only — related repos keep their default branches");
    }

    [Fact]
    public void BuildAgentInputs_session_continuity_outranks_the_launch_BaseBranch()
    {
        // A CONTINUED turn's prior produced branch carries the thread's own work — cloning the operator's original
        // base instead would silently discard every prior turn. The pin applies to the FRESH launch only.
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(new Dictionary<Guid, string> { [Primary] = "run-1/primary" }) with
        {
            Seed = new TaskLaunchSeed { Goal = "g", SurfaceKind = "chat", TeamId = Guid.NewGuid(), BaseBranch = "release/2.x" },
        });

        inputs.GetProperty("baseRef").GetString().ShouldBe("run-1/primary");
        inputs.GetProperty("baseRefFromSession").GetBoolean().ShouldBeTrue("the session ref keeps its SOFT marker — pruned prior branches still fall back");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildAgentInputs_treats_a_blank_launch_BaseBranch_as_absent(string blank)
    {
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(baseRefs: null) with
        {
            Seed = new TaskLaunchSeed { Goal = "g", SurfaceKind = "chat", TeamId = Guid.NewGuid(), BaseBranch = blank },
        });

        inputs.TryGetProperty("baseRef", out _).ShouldBeFalse();
    }

    [Fact]
    public void BuildAgentInputs_ignores_the_launch_BaseBranch_when_no_primary_repo_is_bound()
    {
        var inputs = AgentNodeMapping.BuildAgentInputs(new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = "g", SurfaceKind = "chat", TeamId = Guid.NewGuid(), BaseBranch = "release/2.x" },
            Route = new RoutePlan { ProjectionKind = TaskProjectionKinds.SingleAgent },
        });

        inputs.TryGetProperty("baseRef", out _).ShouldBeFalse("an analysis-only run has nothing to clone — the pin is meaningless without a repo");
    }

    // ── S1: the launch's immutable base vector — PinnedShas threads onto the primary's pinnedSha + each related
    //    entry's pinnedSha, so every participant of the run materializes the SAME base commit. ──

    [Fact]
    public void BuildAgentInputs_threads_per_repo_pins_onto_pinnedSha_and_related_pinnedSha()
    {
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(baseRefs: null) with
        {
            PinnedShas = new Dictionary<Guid, string> { [Primary] = "aaa111aaa111", [Api] = "bbb222bbb222" },
        });

        inputs.GetProperty("pinnedSha").GetString().ShouldBe("aaa111aaa111", "the primary's pin comes from the vector keyed by its repo id");
        inputs.GetProperty("relatedRepositories")[0].GetProperty("pinnedSha").GetString().ShouldBe("bbb222bbb222", "each related repo's pin comes from the vector keyed by ITS repo id — no bleed");
    }

    [Fact]
    public void BuildAgentInputs_omits_pins_for_repos_absent_from_the_vector()
    {
        // The primary was pinned; the related repo was UNPINNABLE (no clone URL / session-soft) → only the primary carries a pin.
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(baseRefs: null) with
        {
            PinnedShas = new Dictionary<Guid, string> { [Primary] = "aaa111aaa111" },
        });

        inputs.GetProperty("pinnedSha").GetString().ShouldBe("aaa111aaa111");
        inputs.GetProperty("relatedRepositories")[0].TryGetProperty("pinnedSha", out _).ShouldBeFalse("a repo absent from the vector is unpinned ⇒ it clones at its ref's tip (legacy)");
    }

    [Fact]
    public void BuildAgentInputs_with_no_pin_vector_omits_pinnedSha_byte_identical()
    {
        var inputs = AgentNodeMapping.BuildAgentInputs(Context(baseRefs: null));

        inputs.TryGetProperty("pinnedSha", out _).ShouldBeFalse("no vector ⇒ no pinnedSha key (tip-of-ref, byte-identical to before S1)");
        inputs.GetProperty("relatedRepositories")[0].TryGetProperty("pinnedSha", out _).ShouldBeFalse("no vector ⇒ no per-repo pin key");
    }
}
