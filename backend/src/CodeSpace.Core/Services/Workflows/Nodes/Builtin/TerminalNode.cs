using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// End-of-workflow marker. Reaching this node successfully sets the run to Success and
/// the engine stops walking. Carries no behaviour of its own — it's the graph-level
/// "the work is done" assertion. Multiple terminals are allowed (e.g. "Approved" + "Rejected"
/// branches each ending in their own terminal).
/// </summary>
public sealed class TerminalNode : INodeRuntime
{
    public string TypeKey => "builtin.terminal";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "End",
        Category = "Logic",
        Kind = NodeKind.Terminal,
        IconKey = "circle-stop",
        Description = "Marks the end of a workflow path.",
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject()
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) => Task.FromResult(NodeResult.Ok());
}
