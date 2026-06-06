using System.Text.Json;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Matches <see cref="PullRequestOpenedEvent"/>. Config schema + match precedence live in
/// <see cref="PrTriggerMatcherFilter"/> — both PR-triggered matchers share that filter so
/// the user-facing matching contract is uniform.
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

        return PrTriggerMatcherFilter.Matches(activationConfig, opened.RepositoryId, opened.Labels);
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
            webUrl = opened.WebUrl,
            labels = opened.Labels,
            isDraft = opened.IsDraft
        };

        return JsonSerializer.SerializeToElement(payload);
    }
}
