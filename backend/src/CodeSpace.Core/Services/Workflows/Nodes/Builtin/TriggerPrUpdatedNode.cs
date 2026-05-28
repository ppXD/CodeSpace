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
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositories": {
                  "type": "array",
                  "x-selector": "trigger.repositories",
                  "default": [],
                  "description": "Each row = one repo + its required labels (AND match).",
                  "items": {
                    "type": "object",
                    "properties": {
                      "repositoryId": {
                        "type": "string",
                        "format": "uuid",
                        "description": "Repository to match."
                      },
                      "labels": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "PR must carry every label listed (case-sensitive)."
                      }
                    },
                    "required": ["repositoryId"],
                    "additionalProperties": false
                  }
                }
              },
              "additionalProperties": false
            }
            """),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": { "type": "string", "format": "uuid" },
                "number": { "type": "integer" },
                "title": { "type": "string" },
                "sourceBranch": { "type": "string" },
                "targetBranch": { "type": "string" },
                "webUrl": { "type": "string" }
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
