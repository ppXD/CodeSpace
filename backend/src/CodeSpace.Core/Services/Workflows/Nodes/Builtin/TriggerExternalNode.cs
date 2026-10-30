using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Accepts runs started by an authenticated external API request.
/// <c>IsManual</c> must remain false: deriveActivations projects an activation row from that
/// flag, and anonymous runs need its lineage or ExecutionAuthorityService rejects every call.
/// </summary>
public sealed class TriggerExternalNode : INodeRuntime
{
    public string TypeKey => ExternalTriggerSlug.TypeKey;

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "External API",
        Category = "Triggers",
        Kind = NodeKind.Trigger,
        IconKey = "webhook",
        IsManual = false,
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "inputSchema": { "type": "object" }
              },
              "additionalProperties": false
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var outputs = context.Scope.Trigger.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(NodeResult.Ok(outputs));
    }
}
