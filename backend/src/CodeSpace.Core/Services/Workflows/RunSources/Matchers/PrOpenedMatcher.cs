using System.Text.Json;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Matches <see cref="PullRequestOpenedEvent"/>. Config schema (mirrors the trigger node's
/// own config):
///   - <c>repositoryId</c>: optional uuid — only fire when the event's RepositoryId matches
///
/// Type key aligns with <c>TriggerPrOpenedNode.TypeKey</c> — the trigger NODE in a definition
/// declares "trigger.pr.opened", and the workflow_activation ROW in the DB shares the same
/// key so the dispatcher can index by the same string.
/// </summary>
public sealed class PrOpenedMatcher : IRunSourceMatcher
{
    public string TypeKey => "trigger.pr.opened";

    public bool Match(NormalizedEvent normalizedEvent, JsonElement activationConfig)
    {
        if (normalizedEvent is not PullRequestOpenedEvent opened) return false;

        return RepositoryFilterMatches(activationConfig, opened.RepositoryId);
    }

    public JsonElement BuildPayload(NormalizedEvent normalizedEvent)
    {
        var opened = (PullRequestOpenedEvent)normalizedEvent;

        // Flatten the event to a stable shape downstream nodes reference with {{ref}} paths
        // like {{trigger.number}} or {{trigger.title}}. We deliberately don't expose every
        // raw field — the schema below is the trigger node's OutputSchema contract.
        var payload = new
        {
            repositoryId = opened.RepositoryId,
            number = opened.Number,
            title = opened.Title,
            body = opened.Body,
            sourceBranch = opened.SourceBranch,
            targetBranch = opened.TargetBranch,
            authorName = opened.AuthorName,
            webUrl = opened.WebUrl
        };

        return JsonSerializer.SerializeToElement(payload);
    }

    private static bool RepositoryFilterMatches(JsonElement activationConfig, Guid eventRepositoryId)
    {
        if (activationConfig.ValueKind != JsonValueKind.Object) return true;
        if (!activationConfig.TryGetProperty("repositoryId", out var repoIdProp)) return true;
        if (repoIdProp.ValueKind == JsonValueKind.Null) return true;
        if (repoIdProp.ValueKind != JsonValueKind.String) return true;
        if (!Guid.TryParse(repoIdProp.GetString(), out var configured)) return true;

        return configured == eventRepositoryId;
    }
}
