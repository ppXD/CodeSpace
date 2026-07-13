using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The NESTED behaviour-fork enums render foolproof: chat.post_message's resolve.mode (how a response wait
/// resolves) as radioCards with a consequence per option — a real fork the reader must understand — and
/// flow.loop's termination.logic (all vs any conditions) as a glanceable segmented and/or. Both live inside a
/// nested object, which SchemaForm renders via a recursive sub-form, so x-control reaches them. Presentation-only
/// hints the engine ignores — the stored enum string is unchanged. Manifests are field initializers, so null!
/// constructor deps are safe for reading the schema.
/// </summary>
[Trait("Category", "Unit")]
public class NestedEnumFoolproofManifestTests
{
    [Fact]
    public void ChatPostMessage_resolve_mode_is_radiocards_with_a_consequence_for_every_option()
    {
        var mode = new ChatPostMessageNode(null!, null!, null!).Manifest.ConfigSchema
            .GetProperty("properties").GetProperty("resolve").GetProperty("properties").GetProperty("mode");

        mode.GetProperty("x-control").GetString().ShouldBe("radioCards", "resolve.mode is a behaviour fork — render it as stacked cards");

        mode.TryGetProperty("x-optionConsequence", out var cons).ShouldBeTrue("resolve.mode must declare x-optionConsequence");
        foreach (var value in mode.GetProperty("enum").EnumerateArray())
        {
            var key = value.GetString()!;
            cons.TryGetProperty(key, out var consequence).ShouldBeTrue($"resolve.mode option '{key}' has no consequence line");
            consequence.GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void FlowLoop_termination_logic_is_a_segmented_and_or()
    {
        var logic = new FlowLoopNode().Manifest.ConfigSchema
            .GetProperty("properties").GetProperty("termination").GetProperty("properties").GetProperty("logic");

        logic.GetProperty("x-control").GetString().ShouldBe("segmented", "and/or is a glanceable two-option toggle — segmented, not a dropdown");
        logic.GetProperty("x-enumLabels").GetProperty("and").GetString().ShouldBe("All conditions match");
    }
}
