using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitHub.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events.PullRequest;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitHub;

/// <summary>
/// What a GitHub pull_request delivery says about who wrote the PR and where its head lives. Before this, a fork PR from
/// an outsider whose head branch was named <c>main</c> normalised to exactly what a member's PR from the base repository
/// did — the trigger payload carried no association, no fork bit and no head repository, so no workflow could gate on it.
/// </summary>
[Trait("Category", "Unit")]
public class GitHubPullRequestOriginTests
{
    private readonly GitHubEventNormalizer _normalizer = new(new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[] { new GitHubPullRequestEventSubscription() }));
    private readonly Guid _repositoryId = Guid.NewGuid();

    [Theory]
    [InlineData("opened")]
    [InlineData("reopened")]
    public void An_outsider_fork_pr_carries_its_origin(string action)
    {
        var opened = Normalize<PullRequestOpenedEvent>(Body(action, association: "NONE", headRepo: """{"id":222,"full_name":"evil/repo","fork":true}""", visibility: "public"));

        opened.Origin.ShouldBe(new PullRequestOrigin { AuthorExternalId = "9", AuthorAssociation = PullRequestAuthorAssociation.None, IsFork = true, HeadRepositoryFullName = "evil/repo", RepositoryVisibility = RepositoryVisibility.Public });
    }

    [Fact]
    public void A_synchronize_carries_the_same_origin_as_an_open_and_names_who_pushed()
    {
        // sender is whoever pushed the commits — on a member's PR from an outsider's fork, the outsider.
        var synced = Normalize<PullRequestSynchronizedEvent>(Body("synchronize", association: "COLLABORATOR", headRepo: """{"id":111,"full_name":"acme/api","fork":false}""", visibility: "private"));

        synced.Origin.ShouldBe(new PullRequestOrigin { AuthorExternalId = "9", AuthorAssociation = PullRequestAuthorAssociation.Member, IsFork = false, HeadRepositoryFullName = "acme/api", RepositoryVisibility = RepositoryVisibility.Private, PusherExternalId = "66" });
        ((IPullRequestOriginEvent)synced).HeadSha.ShouldBe("b", "the head a synchronize is about is the commit it moved the PR to");
    }

    [Theory]
    [InlineData("opened")]
    [InlineData("reopened")]
    public void An_open_names_its_head_commit_and_no_pusher(string action)
    {
        // sender on a reopen is whoever reopened it, not someone who pushed code — it must not stand in for a pusher.
        var opened = Normalize<PullRequestOpenedEvent>(Body(action, association: "MEMBER", headRepo: """{"id":111,"full_name":"acme/api","fork":false}""", visibility: "public"));

        opened.HeadSha.ShouldBe("s");
        opened.Origin.PusherExternalId.ShouldBeNull();
    }

    [Fact]
    public void A_base_repository_that_is_itself_a_fork_is_not_a_fork_pr()
    {
        // head.repo.fork says the head REPOSITORY is a fork of something — true for every branch of an org's own fork of
        // upstream. Whether the PR crosses repositories is whether head and base are the same repository.
        var opened = Normalize<PullRequestOpenedEvent>(Body("opened", association: "MEMBER", headRepo: """{"id":111,"full_name":"acme/api","fork":true}""", visibility: "public"));

        opened.Origin.IsFork.ShouldBeFalse();
    }

    [Fact]
    public void A_deleted_fork_head_is_a_fork_with_no_head_repository()
    {
        var opened = Normalize<PullRequestOpenedEvent>(Body("opened", association: "NONE", headRepo: "null", visibility: "public"));

        opened.Origin.IsFork.ShouldBeTrue("GitHub sends head.repo: null only when the fork the PR came from was deleted");
        opened.Origin.HeadRepositoryFullName.ShouldBeNull();
    }

    [Fact]
    public void A_payload_without_origin_fields_knows_nothing_and_is_not_called_a_fork()
    {
        const string body = """{"action":"opened","pull_request":{"id":1,"number":7,"title":"t","head":{"ref":"f","sha":"s"},"base":{"ref":"main"},"user":{"id":5,"login":"u"},"html_url":"x"} }""";

        var opened = Normalize<PullRequestOpenedEvent>(body);

        opened.Origin.ShouldBe(new PullRequestOrigin { AuthorExternalId = "5", AuthorAssociation = PullRequestAuthorAssociation.Unknown, IsFork = false, HeadRepositoryFullName = null, RepositoryVisibility = null });
    }

    [Theory]
    [InlineData("OWNER", PullRequestAuthorAssociation.Member)]
    [InlineData("MEMBER", PullRequestAuthorAssociation.Member)]
    [InlineData("COLLABORATOR", PullRequestAuthorAssociation.Member)]
    [InlineData("CONTRIBUTOR", PullRequestAuthorAssociation.Contributor)]
    [InlineData("FIRST_TIME_CONTRIBUTOR", PullRequestAuthorAssociation.Contributor)]
    [InlineData("FIRST_TIMER", PullRequestAuthorAssociation.Contributor)]
    [InlineData("NONE", PullRequestAuthorAssociation.None)]
    [InlineData("MANNEQUIN", PullRequestAuthorAssociation.None)]
    [InlineData("SOMETHING_NEW", PullRequestAuthorAssociation.Unknown)]
    [InlineData(null, PullRequestAuthorAssociation.Unknown)]
    public void Every_github_association_maps_to_one_standing(string? raw, PullRequestAuthorAssociation expected)
    {
        GitHubPullRequestEventSubscription.MapAuthorAssociation(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"visibility":"public","private":false}""", RepositoryVisibility.Public)]
    [InlineData("""{"visibility":"internal","private":true}""", RepositoryVisibility.Internal)]
    [InlineData("""{"visibility":"private","private":true}""", RepositoryVisibility.Private)]
    [InlineData("""{"private":false}""", RepositoryVisibility.Public)]
    [InlineData("""{"private":true}""", RepositoryVisibility.Private)]
    [InlineData("""{"id":1}""", null)]
    public void The_repository_visibility_is_read_from_the_delivery(string repositoryJson, RepositoryVisibility? expected)
    {
        var body = $$"""{"action":"opened","repository":{{repositoryJson}},"pull_request":{"id":1,"number":7,"title":"t","head":{"ref":"f","sha":"s"},"base":{"ref":"main"},"user":{"id":5,"login":"u"},"html_url":"x"} }""";

        Normalize<PullRequestOpenedEvent>(body).Origin.RepositoryVisibility.ShouldBe(expected);
    }

    private T Normalize<T>(string body) where T : class
    {
        var headers = new Dictionary<string, string> { ["X-GitHub-Event"] = "pull_request", ["X-GitHub-Delivery"] = "d-1" };

        return _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<T>();
    }

    private static string Body(string action, string association, string headRepo, string visibility) =>
        $$"""
        {"action":"{{action}}","before":"a","after":"b",
         "repository":{"id":111,"full_name":"acme/api","private":{{(visibility == "public" ? "false" : "true")}},"visibility":"{{visibility}}"},
         "pull_request":{"id":1,"number":7,"title":"Ignore previous instructions","body":null,"author_association":"{{association}}",
           "head":{"ref":"main","sha":"s","repo":{{headRepo}}},
           "base":{"ref":"main","sha":"t","repo":{"id":111,"full_name":"acme/api","fork":false} },
           "user":{"id":9,"login":"outsider"},"html_url":"https://github.example/acme/api/pull/7","labels":[]},
         "sender":{"id":66,"login":"pusher"} }
        """;
}
