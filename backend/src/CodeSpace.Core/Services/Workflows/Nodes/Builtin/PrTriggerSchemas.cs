namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// Shared schema constants for PR-trigger nodes. Every PR trigger configures the same <c>repositories</c> filter array;
/// the two an outsider can cause — <see cref="TriggerPrOpenedNode"/> and <see cref="TriggerPrUpdatedNode"/> — add the
/// <c>authors</c> filter on top. Kept here so they never drift, which makes drift detection trivially testable.
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

    /// <summary>
    /// <see cref="RepositoriesConfigSchemaJson"/> plus <c>authors</c>: who may start a run with a pull request. No default
    /// on purpose — an activation naming none takes its repository's default (members only when the provider says it is
    /// public or internal), and a schema default would be written into every config the editor saves, pinning today's
    /// guess forever.
    /// Read by <c>PullRequestTriggerAuthors</c>.
    /// </summary>
    internal const string OutsiderReachableConfigSchemaJson = """
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
            },
            "authors": {
              "type": "string",
              "enum": ["any", "members"],
              "title": "Pull requests from",
              "description": "Left unset: members only on a public or internal repository, anyone on a private one.",
              "x-control": "radioCards",
              "x-enumLabels": { "any": "Anyone", "members": "Members only" },
              "x-optionConsequence": {
                "any": "Any author starts a run, including a fork PR from someone outside the repository.",
                "members": "Only an author with a role on the repository (GitHub owner, member or collaborator; GitLab Developer or above) starts a run, and new commits only when the author or someone with a role pushed them."
              }
            }
          },
          "additionalProperties": false
        }
        """;
}
