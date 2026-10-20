using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class TriggerExternalNodeTests
{
    [Fact]
    public void The_manifest_declares_a_slug_and_an_input_schema_for_the_editor()
    {
        var config = new TriggerExternalNode().Manifest.ConfigSchema;
        config.GetProperty("properties").GetProperty("slug").GetProperty("type").GetString().ShouldBe("string");
        config.GetProperty("required").EnumerateArray().Any(e => e.GetString() == "slug").ShouldBeTrue();
        config.GetProperty("properties").TryGetProperty("inputSchema", out _).ShouldBeTrue();
    }
}
