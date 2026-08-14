using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// Every GitLab event this system can READ must also be one it SUBSCRIBES to. The gap between those
/// two is invisible from inside: the hook registers, reports healthy, and simply never carries the
/// event.
///
/// <para>That is not hypothetical. The flags were derived by substring test — <c>Contains("merge_request")</c>
/// against the raw name <c>"Merge Request Hook"</c>, which does not contain it, because one has a
/// space where the other has an underscore. <c>Push Hook</c> and <c>Issue Hook</c> matched on their
/// first word, so two of three worked. Every GitLab hook ever registered went out with
/// <c>merge_requests_events: false</c> and no pull-request trigger could fire.</para>
/// </summary>
[Trait("Category", "Unit")]
public class GitLabHookEventCoverageTests
{
    /// <summary>
    /// The guard. A new <c>IProviderEventSubscription</c> for GitLab with no flag in
    /// <see cref="GitLabHookEvents"/> fails HERE, rather than by never delivering in production.
    /// </summary>
    [Fact]
    public void Every_gitlab_subscription_can_be_subscribed_to()
    {
        var declared = GitLabSubscriptionNames();

        declared.ShouldNotBeEmpty("If GitLab has no subscriptions this test is not guarding anything — check the discovery below.");

        foreach (var name in declared)
        {
            GitLabHookEvents.All.ShouldContain(name,
                customMessage: $"'{name}' is normalised by a GitLab subscription but has no hook flag, so every hook " +
                               $"registers without it and the event never arrives. Add it to {nameof(GitLabHookEvents)}.");
        }
    }

    /// <summary>The other direction: a flag for an event nothing reads would subscribe to deliveries that only get rejected.</summary>
    [Fact]
    public void Every_subscribable_event_is_one_we_can_read()
    {
        var declared = GitLabSubscriptionNames();

        foreach (var name in GitLabHookEvents.All)
        {
            declared.ShouldContain(name,
                customMessage: $"'{name}' can be subscribed to but nothing normalises it, so its deliveries arrive only " +
                               "to be rejected.");
        }
    }

    /// <summary>
    /// The exact defect, pinned. This is the assertion that would have caught it: a real subscription
    /// set in, and merge requests actually subscribed.
    /// </summary>
    [Fact]
    public void A_merge_request_subscription_actually_sets_the_merge_request_flag()
    {
        var flags = GitLabHookEvents.Flags(new[] { GitLabHookEvents.Push, GitLabHookEvents.MergeRequest, GitLabHookEvents.Issue });

        flags.Push.ShouldBeTrue();
        flags.Issues.ShouldBeTrue();
        flags.MergeRequests.ShouldBeTrue(
            customMessage: "Merge requests must be subscribed, or no pull-request trigger can ever fire. This is the " +
                           "flag a substring test silently left false on every hook.");
    }

    [Fact]
    public void An_unsubscribed_event_stays_unsubscribed()
    {
        var flags = GitLabHookEvents.Flags(new[] { GitLabHookEvents.Push });

        flags.Push.ShouldBeTrue();
        flags.MergeRequests.ShouldBeFalse();
        flags.Issues.ShouldBeFalse();
    }

    /// <summary>An unmappable name is a hook that would not carry it — loud beats silent.</summary>
    [Fact]
    public void An_event_with_no_flag_refuses_rather_than_subscribing_to_nothing()
    {
        var thrown = Should.Throw<ArgumentException>(() => GitLabHookEvents.Flags(new[] { "Pipeline Hook" }));

        thrown.Message.ShouldContain("Pipeline Hook");
    }

    /// <summary>Reading a hook back must describe its events the way the subscriptions name them, not in a fourth spelling.</summary>
    [Fact]
    public void Reading_a_hook_back_returns_the_names_the_subscriptions_use()
    {
        var names = GitLabHookEvents.Names(push: true, mergeRequests: true, issues: false);

        names.ShouldBe(new[] { GitLabHookEvents.Push, GitLabHookEvents.MergeRequest });
        GitLabHookEvents.Flags(names).MergeRequests.ShouldBeTrue("Names and Flags must round-trip, or a hook read back looks like it subscribes to less than it does.");
    }

    /// <summary>Discovered from the assembly so the guard covers subscriptions nobody remembered to list here.</summary>
    private static IReadOnlyList<string> GitLabSubscriptionNames() =>
        typeof(GitLabHookEvents).Assembly.GetTypes()
            .Where(t => typeof(IProviderEventSubscription).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IProviderEventSubscription)Activator.CreateInstance(t)!)
            .Where(s => s.Kind == ProviderKind.GitLab)
            .Select(s => s.RawEventName)
            .ToList();
}
