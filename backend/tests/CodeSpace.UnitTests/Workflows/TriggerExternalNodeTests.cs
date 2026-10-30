using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class TriggerExternalNodeTests
{
    [Fact]
    public void The_manifest_declares_an_input_schema_without_a_route_name()
    {
        var config = new TriggerExternalNode().Manifest.ConfigSchema;
        config.GetProperty("properties").TryGetProperty("slug", out _).ShouldBeFalse();
        config.GetProperty("properties").TryGetProperty("inputSchema", out _).ShouldBeTrue();
    }
}
