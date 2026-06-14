using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// The body-entry marker for a <c>flow.map</c> container (mirrors <c>flow.loop_start</c>). It carries
/// no config and emits no outputs — its only job is to be the single root of the map's body subgraph,
/// so the engine knows where each element-branch's sub-walk begins. Every body node
/// (<c>NodeDefinition.ParentId == mapId</c>) descends from it.
///
/// <para>A plain passthrough: <c>RunAsync</c> returns an empty success. The per-element Iteration scope
/// (<c>{{item}}</c> / <c>{{index}}</c>) is already populated by the engine before the branch body runs,
/// so downstream body nodes read it directly — this marker doesn't need to expose anything.</para>
/// </summary>
public sealed class FlowMapStartNode : INodeRuntime
{
    public string TypeKey => "flow.map_start";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Map start",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "play",
        Description = "Entry point of a map body. Added automatically with a Map container; not placed on its own.",
        ConfigSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        InputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }""")
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NodeResult.Ok(new Dictionary<string, System.Text.Json.JsonElement>()));
}
