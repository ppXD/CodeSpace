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
}
