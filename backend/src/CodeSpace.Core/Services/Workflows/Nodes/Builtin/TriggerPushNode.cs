using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// "Run when commits are pushed" trigger. Matching against incoming NormalizedEvents happens in
/// <c>PushMatcher</c>; this node exposes the trigger payload (set on the engine scope by the
/// dispatcher before the run starts) as its outputs so downstream nodes can
/// <c>{{trigger.branch}}</c>-style read it.
///
/// Config is a single optional repository + an optional branch list (OR-match) — distinct from
/// the PR triggers' repo+label shape because a push has no labels. Both fields render with the
/// generic SchemaForm (repository picker + comma-separated branch list); no bespoke editor.
/// </summary>
public sealed class TriggerPushNode : INodeRuntime
{
    public string TypeKey => "trigger.push";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "When commits pushed",
        Category = "Triggers",
        Kind = NodeKind.Trigger,
        IconKey = "git-commit",
        Description = "Starts the workflow when commits are pushed to a repository.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "repositoryId": {
                  "type": "string",
                  "format": "uuid",
                  "x-selector": "repository",
                  "description": "Repository to watch. Leave empty to match pushes to any connected repository."
                },
                "branches": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Branch names to match (any one fires). Leave empty to match every branch."
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
                "ref": { "type": "string" },
                "branch": { "type": "string" },
                "beforeSha": { "type": "string" },
                "afterSha": { "type": "string" },
                "pusherName": { "type": "string" },
                "commitCount": { "type": "integer" }
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
