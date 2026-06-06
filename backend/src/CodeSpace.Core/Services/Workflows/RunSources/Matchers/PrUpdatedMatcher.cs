using System.Text.Json;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.PullRequest;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Matches <see cref="PullRequestSynchronizedEvent"/>. Payload deliberately mirrors the
/// shape of pr.opened so workflows that target either trigger can share the same downstream
/// node graph (the {{trigger.repositoryId}} / {{trigger.number}} refs resolve identically).
/// Match precedence + config schema live in <see cref="PrTriggerMatcherFilter"/>.
/// </summary>
public sealed class PrUpdatedMatcher : IRunSourceMatcher
{
    public string TypeKey => "trigger.pr.updated";

    public bool Match(NormalizedEvent normalizedEvent, JsonElement activationConfig)
    {
        if (normalizedEvent is not PullRequestSynchronizedEvent synced) return false;

        return PrTriggerMatcherFilter.Matches(activationConfig, synced.RepositoryId, synced.Labels);
    }

    public JsonElement BuildPayload(NormalizedEvent normalizedEvent)
    {
        var synced = (PullRequestSynchronizedEvent)normalizedEvent;

        var payload = new
        {
            repositoryId = synced.RepositoryId,
            number = synced.Number,
            previousHeadSha = synced.PreviousHeadSha,
            newHeadSha = synced.NewHeadSha,
            labels = synced.Labels,
            isDraft = synced.IsDraft
        };

        return JsonSerializer.SerializeToElement(payload);
    }
}
