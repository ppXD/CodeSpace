using System.Text.Json;
using CodeSpace.Messages.Events;
using CodeSpace.Messages.Events.Push;

namespace CodeSpace.Core.Services.Workflows.RunSources.Matchers;

/// <summary>
/// Matches <see cref="PushReceivedEvent"/>. Filtering (repository + branch OR-match) lives in
/// <see cref="PushTriggerMatcherFilter"/> — a separate filter from the PR triggers because a push
/// has no labels; its axes are repository + branch.
///
/// Type key aligns with <c>TriggerPushNode.TypeKey</c> ("trigger.push"); the
/// <c>workflow_activation</c> row shares that string so the dispatcher indexes by it.
/// </summary>
public sealed class PushMatcher : IRunSourceMatcher
{
    public string TypeKey => "trigger.push";

    public bool Match(NormalizedEvent normalizedEvent, JsonElement activationConfig)
    {
        if (normalizedEvent is not PushReceivedEvent push) return false;

        return PushTriggerMatcherFilter.Matches(activationConfig, push.RepositoryId, push.Ref);
    }

    public JsonElement BuildPayload(NormalizedEvent normalizedEvent)
    {
        var push = (PushReceivedEvent)normalizedEvent;

        var payload = new
        {
            repositoryId = push.RepositoryId,
            @ref = push.Ref,
            branch = ShortBranch(push.Ref),
            beforeSha = push.BeforeSha,
            afterSha = push.AfterSha,
            pusherName = push.PusherName,
            commitCount = push.Commits.Count
        };

        return JsonSerializer.SerializeToElement(payload);
    }

    private const string BranchRefPrefix = "refs/heads/";

    /// <summary>Short branch name (<c>refs/heads/main</c> → <c>main</c>) for the convenient
    /// <c>{{trigger.branch}}</c> ref; the full <c>ref</c> stays available for tag/other-ref cases.</summary>
    private static string ShortBranch(string gitRef) =>
        gitRef.StartsWith(BranchRefPrefix, StringComparison.Ordinal) ? gitRef[BranchRefPrefix.Length..] : gitRef;
}
