using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// "Run when a Pull Request is merged" trigger. Matching against incoming NormalizedEvents
/// happens in <c>PrMergedMatcher</c>; this node exposes the trigger payload (set on the
/// engine scope by the dispatcher before the run starts) as its outputs so downstream nodes
/// can <c>{{trigger.mergeCommitSha}}</c>-style read it.
///
/// Shares the <c>repositories</c> filter config schema with the opened / updated triggers
/// (<see cref="PrTriggerSchemas.RepositoriesConfigSchemaJson"/>) — same repo + AND-label
/// matching, so "deploy when a PR labelled <c>release</c> merges" is a one-row config.
/// </summary>
public sealed class TriggerPrMergedNode : INodeRuntime
{
    public string TypeKey => "trigger.pr.merged";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "When PR merged",
        Category = "Triggers",
        Kind = NodeKind.Trigger,
        IconKey = "git-merge",
        Description = "Starts the workflow when a pull/merge request is merged.",
        ConfigSchema = SchemaBuilder.Parse(PrTriggerSchemas.RepositoriesConfigSchemaJson),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid" },
                "number": { "type": "integer" },
                "mergedByName": { "type": "string" },
                "mergeCommitSha": { "type": ["string","null"] },
                "labels": { "type": "array", "items": { "type": "string" } }
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
