using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The consumer's view of <c>git.integrate_run</c>'s declared outputs. <c>DefinitionValidator</c> rejects a
/// <c>{{nodes.X.outputs.Y}}</c> reference to a key X's OutputSchema does not declare, so a key the node EMITS but does
/// not DECLARE is unreachable to every graph that wants it: the plan-map synth cannot bind <c>summary</c>, and no
/// author can bind the review trail a resumed pass already emits (<c>reviewApproved</c> / <c>reviewComment</c> /
/// <c>reviewedBy</c>). Pinned one key per case so a failure names exactly the key that is missing.
/// </summary>
[Trait("Category", "Unit")]
public class GitIntegrateRunNodeManifestTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("integratedBranch")]
    [InlineData("appliedCount")]
    [InlineData("reason")]
    [InlineData("conflicts")]
    [InlineData("summary")]
    [InlineData("withheld")]
    [InlineData("reviewApproved")]
    [InlineData("reviewComment")]
    [InlineData("reviewedBy")]
    public void A_downstream_node_can_bind_every_output_the_node_emits(string output)
    {
        var validator = new DefinitionValidator(new NodeRegistry(new INodeRuntime[] { new TriggerManualNode(), new GitIntegrateRunNode(null!, null!, null!, null!), new TerminalNode() }));

        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("start", "trigger.manual", new { }),
                Node("integrate", "git.integrate_run", new { repositoryId = Guid.NewGuid().ToString() }),
                Node("done", "builtin.terminal", new { bound = "{{nodes.integrate.outputs." + output + "}}" }),
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "integrate" }, new() { From = "integrate", To = "done" } },
        };

        var validation = validator.Validate(definition);

        validation.IsValid.ShouldBeTrue(customMessage: $"'{output}' must be a declared output of git.integrate_run — errors: " + string.Join(" | ", validation.Errors));
    }

    private static NodeDefinition Node(string id, string typeKey, object inputs) => new()
    {
        Id = id,
        TypeKey = typeKey,
        Config = JsonDocument.Parse("{}").RootElement.Clone(),
        Inputs = JsonSerializer.SerializeToElement(inputs),
    };
}
