using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>"Run on PR commits-pushed (synchronize) events." Same shape as <c>TriggerPrOpenedNode</c>.</summary>
public sealed class TriggerPrUpdatedNode : INodeRuntime
{
    public string TypeKey => "trigger.pr.updated";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "When PR updated",
        Category = "Triggers",
        Kind = NodeKind.Trigger,
        IconKey = "git-commit-horizontal",
        Description = "Starts the workflow when new commits are pushed to a pull/merge request.",
        ConfigSchema = SchemaBuilder.Parse(PrTriggerSchemas.RepositoriesConfigSchemaJson),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid" },
                "number": { "type": "integer" },
                "previousHeadSha": { "type": "string" },
                "newHeadSha": { "type": "string" },
                "labels": { "type": "array", "items": { "type": "string" } },
                "isDraft": { "type": "boolean" }
              }
            }
            """)
    };

    public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var outputs = context.Scope.Trigger.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(NodeResult.Ok(outputs));
    }
}
