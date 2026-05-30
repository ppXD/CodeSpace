using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// The body-entry marker for a <c>flow.loop</c> container (mirrors Dify's loop-start "🏠" node).
/// It carries no config and emits no outputs — its only job is to be the single root of the loop's
/// body subgraph, so the engine knows where each iteration's sub-walk begins. Every body node
/// (<c>NodeDefinition.ParentId == loopId</c>) descends from it.
///
/// <para>A plain passthrough: <c>RunAsync</c> returns an empty success. The loop scope (<c>loop.*</c>)
/// is already populated by the engine before the body runs, so downstream body nodes read
/// <c>{{loop.&lt;var&gt;}}</c> directly — this marker doesn't need to expose anything.</para>
/// </summary>
public sealed class FlowLoopStartNode : INodeRuntime
{
    public string TypeKey => "flow.loop_start";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Loop start",
        Category = "Logic",
        Kind = NodeKind.Regular,
        IconKey = "play",
        Description = "Entry point of a loop body. Added automatically with a Loop container; not placed on its own.",
        ConfigSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        InputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }"""),
        OutputSchema = SchemaBuilder.Parse("""{ "type": "object", "properties": {} }""")
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NodeResult.Ok(new Dictionary<string, System.Text.Json.JsonElement>()));
}
