using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// "Run when a Pull Request is opened" trigger. The actual matching against incoming
/// NormalizedEvents happens in <c>PrOpenedMatcher</c>; this node's only job is to expose
/// the trigger payload (set on the engine's scope by the dispatcher BEFORE the run starts)
/// as its outputs so downstream nodes can <c>{{trigger.title}}</c>-style read it.
///
/// Because the engine already wires the trigger payload onto <c>scope.Trigger</c> directly,
/// this node is effectively a pass-through: it copies the trigger scope into its outputs
/// so the unified <c>nodes.&lt;id&gt;.outputs.*</c> path also works for nodes that prefer
/// that syntax.
/// </summary>
public sealed class TriggerPrOpenedNode : INodeRuntime
{
    public string TypeKey => "trigger.pr.opened";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "When PR opened",
        Category = "Triggers",
        Kind = NodeKind.Trigger,
        IconKey = "git-pull-request",
        Description = "Starts the workflow when a pull/merge request is opened.",
        ConfigSchema = SchemaBuilder.Parse(PrTriggerSchemas.RepositoriesConfigSchemaJson),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid" },
                "number": { "type": "integer" },
                "title": { "type": "string" },
                "body": { "type": ["string","null"] },
                "sourceBranch": { "type": "string" },
                "targetBranch": { "type": "string" },
                "authorName": { "type": "string" },
                "webUrl": { "type": "string" },
                "labels": { "type": "array", "items": { "type": "string" } },
                "isDraft": { "type": "boolean" }
              }
            }
            """)
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        // Trigger nodes echo their scope.Trigger bag as outputs so downstream {{ref}} paths
        // can use either `trigger.x` or `nodes.<this-id>.outputs.x` interchangeably.
        var outputs = context.Scope.Trigger.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(NodeResult.Ok(outputs));
    }
}
