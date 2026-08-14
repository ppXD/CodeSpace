using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// Hooks subscribe to everything the provider offers, not to the handful of events this system
/// currently reads.
///
/// <para>The reason is that a hook cannot be re-synced once registered. A hook narrowed to today's
/// events would need a hand visit — on every installation, for every repository — the day a
/// capability lands that reads one more. Subscribing wide once is the cheaper side of that trade,
/// and the deliberate cost is deliveries nothing acts on, which collapse to one audit row per event
/// type per day instead of accumulating.</para>
///
/// <para>These assertions are the record of that decision. Narrowing either list is a choice someone
/// has to make here, in front of the reason, rather than by editing a registration call.</para>
/// </summary>
[Trait("Category", "Unit")]
public class HookSubscriptionBreadthTests
{
    /// <summary>
    /// GitHub documents a wildcard that also covers event types it adds later, which is the only
    /// subscription that stays correct with nobody returning to it.
    /// </summary>
    [Fact]
    public void GitHub_subscribes_with_the_wildcard()
    {
        GitHubHookEvents.All.ShouldBe(new[] { "*" },
            customMessage: "GitHub's wildcard is what makes a hook registered today still deliver an event type " +
                           "GitHub introduces next year. Enumerating events means revisiting every hook that exists.");
    }

    /// <summary>
    /// GitLab has no wildcard — the API is a boolean per event — so the full documented set is
    /// enumerated, and merge requests must be in it. That flag is the one that was silently false on
    /// every hook this system ever registered.
    /// </summary>
    [Theory]
    [InlineData("push_events")]
    [InlineData("merge_requests_events")]
    [InlineData("issues_events")]
    [InlineData("tag_push_events")]
    [InlineData("note_events")]
    [InlineData("pipeline_events")]
    [InlineData("releases_events")]
    [InlineData("deployment_events")]
    public void A_gitlab_project_hook_subscribes_to_every_documented_event(string attribute)
    {
        GitLabHookEvents.ProjectHookAttributes.ShouldContain(attribute);
    }

    /// <summary>Group hooks accept four attributes the project endpoint does not; sending the project body would silently under-subscribe.</summary>
    [Theory]
    [InlineData("subgroup_events")]
    [InlineData("member_events")]
    [InlineData("project_events")]
    [InlineData("milestone_events")]
    public void A_gitlab_group_hook_subscribes_to_the_group_only_events_too(string attribute)
    {
        GitLabHookEvents.GroupHookAttributes.ShouldContain(attribute);
        GitLabHookEvents.ProjectHookAttributes.ShouldNotContain(attribute,
            customMessage: $"'{attribute}' is a group-only attribute; sending it to the project endpoint claims a subscription that does not exist.");
    }

    [Fact]
    public void The_group_set_is_a_superset_of_the_project_set()
    {
        foreach (var attribute in GitLabHookEvents.ProjectHookAttributes)
            GitLabHookEvents.GroupHookAttributes.ShouldContain(attribute);
    }

    /// <summary>Every event this system can READ must be inside the set it subscribes to, or the hook cannot carry it.</summary>
    [Fact]
    public void Everything_we_can_read_is_inside_what_we_subscribe_to()
    {
        var readable = new Dictionary<string, string>
        {
            [GitLabHookEvents.Push] = "push_events",
            [GitLabHookEvents.MergeRequest] = "merge_requests_events",
            [GitLabHookEvents.Issue] = "issues_events",
        };

        foreach (var (rawName, attribute) in readable)
        {
            GitLabHookEvents.ProjectHookAttributes.ShouldContain(attribute,
                customMessage: $"'{rawName}' is normalised by a subscription but '{attribute}' is not requested, so the hook never carries it.");
        }
    }
}
