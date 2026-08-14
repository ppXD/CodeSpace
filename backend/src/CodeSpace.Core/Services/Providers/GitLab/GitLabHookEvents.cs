namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// The one place that knows how a GitLab event is named and which API flag subscribes to it.
///
/// <para>GitLab spells the same event three ways: <c>X-Gitlab-Event: Merge Request Hook</c> on the
/// delivery, <c>merge_requests_events</c> on the hook API, and <c>merge_request</c> in prose. The
/// registration code used to bridge those with a substring test — <c>e.Contains("merge_request")</c>
/// against the raw name — and "Merge Request Hook" does not contain "merge_request", because one has
/// a space where the other has an underscore. Push and Issue happened to survive the same test on
/// their first word, so two of three worked and the third silently did not: every GitLab hook this
/// system ever registered went out with <c>merge_requests_events: false</c>, and no pull-request
/// trigger could fire. Nothing failed, nothing was logged, and the hook looked healthy.</para>
///
/// <para>So the mapping is exact and total. Unknown names throw rather than resolve to "subscribe to
/// nothing": a hook registered without an event it was meant to carry is the failure above, and it
/// has to be loud. <c>GitLabHookEventCoverageTests</c> holds the other end — a subscription added
/// without a flag here fails there rather than in production months later.</para>
/// </summary>
public static class GitLabHookEvents
{
    /// <summary>Value of <c>X-Gitlab-Event</c>, and what the matching <c>IProviderEventSubscription</c> declares.</summary>
    public const string Push = "Push Hook";

    /// <summary>Value of <c>X-Gitlab-Event</c>, and what the matching <c>IProviderEventSubscription</c> declares.</summary>
    public const string MergeRequest = "Merge Request Hook";

    /// <summary>Value of <c>X-Gitlab-Event</c>, and what the matching <c>IProviderEventSubscription</c> declares.</summary>
    public const string Issue = "Issue Hook";

    /// <summary>Every event this system can both subscribe to and read.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Push, MergeRequest, Issue };

    /// <summary>
    /// Which flags to send for a subscription set. Exact matching on the raw names — a name that is
    /// not one of <see cref="All"/> throws, because the alternative is a hook that quietly does not
    /// carry it.
    /// </summary>
    public static GitLabHookFlags Flags(IEnumerable<string> subscribedEvents)
    {
        var events = subscribedEvents.ToList();

        var unknown = events.Where(e => !All.Contains(e, StringComparer.Ordinal)).ToList();

        if (unknown.Count > 0)
            throw new ArgumentException($"No GitLab hook flag is defined for {string.Join(", ", unknown)}. Add it to {nameof(GitLabHookEvents)} — registering the hook without it would subscribe to nothing and fail silently.", nameof(subscribedEvents));

        return new GitLabHookFlags(
            Push: events.Contains(Push, StringComparer.Ordinal),
            MergeRequests: events.Contains(MergeRequest, StringComparer.Ordinal),
            Issues: events.Contains(Issue, StringComparer.Ordinal));
    }

    /// <summary>
    /// The reverse, for reading a hook back off the provider. Returns the SAME raw names the
    /// subscriptions declare, so a hook read from GitLab and a hook we staged locally describe their
    /// events identically — they used to differ ("merge_request" versus "Merge Request Hook"), which
    /// is a fourth spelling of the same drift.
    /// </summary>
    public static List<string> Names(bool push, bool mergeRequests, bool issues)
    {
        var names = new List<string>(3);

        if (push) names.Add(Push);
        if (mergeRequests) names.Add(MergeRequest);
        if (issues) names.Add(Issue);

        return names;
    }
}

/// <summary>The booleans GitLab's hook API takes, named as it names them.</summary>
public readonly record struct GitLabHookFlags(bool Push, bool MergeRequests, bool Issues);
