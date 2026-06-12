using System.Text.Json;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Matches <see cref="PullRequestMergedEvent"/>. Reuses the shared repository + label filter
/// (<see cref="PrTriggerMatcherFilter"/>) exactly like the opened / updated matchers, so the
/// user-facing matching contract — repository scope + AND-label filter — is uniform across
/// every PR trigger. The classic use is "run when a PR labelled <c>release</c> merges".
///
/// Type key aligns with <c>TriggerPrMergedNode.TypeKey</c> ("trigger.pr.merged"); the
/// <c>workflow_activation</c> row shares that string so the dispatcher indexes by it.
/// </summary>
public sealed class PrMergedMatcher : IRunSourceMatcher
{
    public string TypeKey => "trigger.pr.merged";

    public bool Match(NormalizedEvent normalizedEvent, JsonElement activationConfig)
    {
        if (normalizedEvent is not PullRequestMergedEvent merged) return false;

        return PrTriggerMatcherFilter.Matches(activationConfig, merged.RepositoryId, merged.Labels);
    }

    public JsonElement BuildPayload(NormalizedEvent normalizedEvent)
    {
        var merged = (PullRequestMergedEvent)normalizedEvent;

        var payload = new
        {
            repositoryId = merged.RepositoryId,
            number = merged.Number,
            mergedByName = merged.MergedByName,
            mergeCommitSha = merged.MergeCommitSha,
            labels = merged.Labels
        };

        return JsonSerializer.SerializeToElement(payload);
    }
}
