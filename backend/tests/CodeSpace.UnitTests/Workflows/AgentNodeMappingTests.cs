using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Projection.Builders;
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
}
