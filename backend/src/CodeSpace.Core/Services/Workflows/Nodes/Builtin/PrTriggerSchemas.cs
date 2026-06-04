namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Shared schema constants for PR-trigger nodes. Both <see cref="TriggerPrOpenedNode"/> and
/// <see cref="TriggerPrUpdatedNode"/> configure the same <c>repositories</c> filter array —
/// keeping it here ensures they never drift and makes drift detection trivially testable.
/// </summary>
internal static class PrTriggerSchemas
{
    /// <summary>
    /// JSON Schema for the shared <c>repositories</c> filter: an array where each row picks a
    /// repository + optional label requirements. Rendered by the editor via the
    /// <c>x-selector: "trigger.repositories"</c> custom component.
    /// </summary>
    internal const string RepositoriesConfigSchemaJson = """
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
        """;
}
