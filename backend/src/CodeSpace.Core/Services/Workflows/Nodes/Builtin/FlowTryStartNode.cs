using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// The body-entry marker for a <c>flow.try</c> container (mirrors <c>flow.loop_start</c>). It carries
/// no config and emits no outputs — its only job is to be the single root of the try's body subgraph,
/// so the engine knows where the body sub-walk begins. Every body node
/// (<c>NodeDefinition.ParentId == tryId</c>) descends from it.
///
/// <para>A plain passthrough: <c>RunAsync</c> returns an empty success. Added automatically with a Try
/// container; not placed on its own.</para>
/// </summary>
public sealed class FlowTryStartNode : INodeRuntime
{
    public string TypeKey => "flow.try_start";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Try start",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "play",
        Description = "Entry point of a try body. Added automatically with a Try container; not placed on its own.",
        ConfigSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        InputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }""")
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NodeResult.Ok(new Dictionary<string, System.Text.Json.JsonElement>()));
}
