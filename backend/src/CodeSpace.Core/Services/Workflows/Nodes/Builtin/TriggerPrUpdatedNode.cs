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
                  "description": "Repositories this trigger fires on. Each entry binds a repo to its own label filter. Leave empty to match nothing; omit the key entirely to match every repo bound to this team.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "repositoryId": {
                        "type": "string",
                        "format": "uuid",
                        "x-selector": "repository",
                        "description": "Repository to match."
                      },
                      "labels": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "Only fire when the PR carries every label listed here (case-sensitive). Leave empty / omit to ignore labels for this repo."
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
