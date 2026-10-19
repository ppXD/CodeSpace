using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// When two of a team's provider connections are the same host: the one definition a Re-link dialog, a relink command, a connect's carry-forward and
/// the repair sweep share. Same team, same provider, API roots that are the same service — <c>ApiUrl</c>, or the base URL when unset — and
/// nothing else. A second OAuth application on one GitLab is a second connection with the same root; another host, port or path is not.
/// </summary>
[Trait("Category", "Unit")]
public class ProviderInstanceHostTests
{
    private static readonly Guid Team = Guid.NewGuid();

    [Theory]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test", null, true)]
    [InlineData("https://gitlab.acme.test", null, "https://GITLAB.acme.test/", null, true)]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test", "https://gitlab.acme.test", true)]     // an API URL that says what the base URL would
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test/", "https://gitlab.acme.test", null, true)]
    [InlineData("https://gitlab.acme.test", "https://api.acme.test", "https://gitlab.acme.test", "https://api.acme.test", true)]
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test/gitlab", null, false)]                  // another path
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.acme.test:8443", null, false)]                    // another port
    [InlineData("https://gitlab.acme.test", null, "http://gitlab.acme.test", null, false)]                          // another scheme
    [InlineData("https://gitlab.acme.test", null, "https://gitlab.other.test", null, false)]                        // another host
    [InlineData("https://gitlab.acme.test", "https://api.acme.test", "https://gitlab.acme.test", null, false)]      // the same base URL, calls going elsewhere: what a repository is read through is the API
    public void Two_connections_are_interchangeable_when_their_API_roots_are_the_same_service(string baseA, string? apiA, string baseB, string? apiB, bool expected)
    {
        var a = Instance(baseA, apiA);
        var b = Instance(baseB, apiB);

        ProviderInstanceHost.AreInterchangeable(a, b).ShouldBe(expected);
        ProviderInstanceHost.AreInterchangeable(b, a).ShouldBe(expected, "symmetric");
    }

    [Fact]
    public void A_connection_is_interchangeable_with_itself_and_a_different_team_or_provider_never_is()
    {
        var gitlab = Instance("https://gitlab.acme.test");

        ProviderInstanceHost.AreInterchangeable(gitlab, gitlab).ShouldBeTrue();
        ProviderInstanceHost.AreInterchangeable(gitlab, With(Instance("https://gitlab.acme.test"), i => i.TeamId = Guid.NewGuid())).ShouldBeFalse("another team's connection is another team's");
        ProviderInstanceHost.AreInterchangeable(gitlab, With(Instance("https://gitlab.acme.test"), i => i.Provider = ProviderKind.GitHub)).ShouldBeFalse("the same address under another provider is not the same service");
    }

    [Fact]
    public void Groups_are_the_sets_of_two_or_more_and_a_lone_connection_is_in_none()
    {
        var one = Instance("https://gitlab.acme.test");
        var sameHost = Instance("https://gitlab.acme.test/");
        var alsoSameHost = Instance("https://GITLAB.acme.test");
        var lone = Instance("https://gitlab.other.test");
        var otherTeams = With(Instance("https://gitlab.acme.test"), i => i.TeamId = Guid.NewGuid());
        var pair = Instance("https://gitlab.third.test");
        var pairToo = Instance("https://gitlab.third.test");

        var groups = ProviderInstanceHost.GroupsOf(new[] { one, lone, sameHost, otherTeams, pair, alsoSameHost, pairToo });

        groups.Count.ShouldBe(2);
        groups.Single(g => g.Contains(one.Id)).OrderBy(id => id).ShouldBe(new[] { one.Id, sameHost.Id, alsoSameHost.Id }.OrderBy(id => id), "three connections to one host");
        groups.Single(g => g.Contains(pair.Id)).OrderBy(id => id).ShouldBe(new[] { pair.Id, pairToo.Id }.OrderBy(id => id));
        groups.SelectMany(g => g).ShouldNotContain(lone.Id);
        groups.SelectMany(g => g).ShouldNotContain(otherTeams.Id, "another team's connection to the same host is in no group of this team's");
    }

    [Fact]
    public void The_groups_do_not_depend_on_the_order_the_connections_are_read_in()
    {
        var instances = Enumerable.Range(0, 6).Select(i => Instance(i % 2 == 0 ? "https://gitlab.acme.test" : "https://gitlab.third.test")).ToList();

        var forward = ProviderInstanceHost.GroupsOf(instances).Select(g => g.OrderBy(id => id).ToList()).OrderBy(g => g[0]).ToList();
        var backward = ProviderInstanceHost.GroupsOf(Enumerable.Reverse(instances)).Select(g => g.OrderBy(id => id).ToList()).OrderBy(g => g[0]).ToList();

        forward.Select(g => string.Join(",", g)).ShouldBe(backward.Select(g => string.Join(",", g)));
    }

    private static ProviderInstance Instance(string baseUrl, string? apiUrl = null) => new() { Id = Guid.NewGuid(), TeamId = Team, Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = baseUrl, ApiUrl = apiUrl };

    private static ProviderInstance With(ProviderInstance instance, Action<ProviderInstance> change)
    {
        change(instance);

        return instance;
    }
}
