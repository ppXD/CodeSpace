using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.GitLab.Events;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events.PullRequest;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// What a GitLab Merge Request Hook says about who wrote the MR and where its source branch lives. GitLab's payload names
/// the author but not their standing, so the association stays Unknown here and is looked up at dispatch; the fork bit,
/// the source project and the project's visibility are all on the payload.
/// </summary>
[Trait("Category", "Unit")]
public class GitLabMergeRequestOriginTests
{
    private readonly GitLabEventNormalizer _normalizer = new(new ProviderEventSubscriptionRegistry(new IProviderEventSubscription[] { new GitLabMergeRequestEventSubscription() }));
    private readonly Guid _repositoryId = Guid.NewGuid();

    [Theory]
    [InlineData("open")]
    [InlineData("reopen")]
    public void A_merge_request_from_a_fork_carries_its_origin(string action)
    {
        var opened = Normalize<PullRequestOpenedEvent>(Body(action, sourceProjectId: 16, visibilityLevel: 20));

        opened.Origin.ShouldBe(new PullRequestOrigin { AuthorExternalId = "51", AuthorAssociation = PullRequestAuthorAssociation.Unknown, IsFork = true, HeadRepositoryFullName = "evil/api", RepositoryVisibility = RepositoryVisibility.Public });
    }

    [Fact]
    public void The_author_is_the_merge_requests_author_not_whoever_reopened_it()
    {
        // user is the actor of THIS delivery; a maintainer reopening an outsider's MR must not lend the MR their standing.
        var opened = Normalize<PullRequestOpenedEvent>(Body("reopen", sourceProjectId: 15, visibilityLevel: 0));

        opened.AuthorName.ShouldBe("maintainer");
        opened.Origin.AuthorExternalId.ShouldBe("51");
        opened.Origin.IsFork.ShouldBeFalse();
    }

    [Fact]
    public void A_code_push_carries_the_same_origin_and_names_who_pushed()
    {
        // On an update that moved the head, user is whoever pushed — the one GitLab delivery where the actor IS the
        // person who made the code what it is.
        var synced = Normalize<PullRequestSynchronizedEvent>(Body("update", sourceProjectId: 16, visibilityLevel: 10, oldrev: "\"abc\""));

        synced.Origin.ShouldBe(new PullRequestOrigin { AuthorExternalId = "51", AuthorAssociation = PullRequestAuthorAssociation.Unknown, IsFork = true, HeadRepositoryFullName = "evil/api", RepositoryVisibility = RepositoryVisibility.Internal, PusherExternalId = "7" });
        ((IPullRequestOriginEvent)synced).HeadSha.ShouldBe("def");
    }

    [Theory]
    [InlineData("open")]
    [InlineData("reopen")]
    public void An_open_names_its_head_commit_and_no_pusher(string action)
    {
        var opened = Normalize<PullRequestOpenedEvent>(Body(action, sourceProjectId: 16, visibilityLevel: 20));

        opened.HeadSha.ShouldBe("def");
        opened.Origin.PusherExternalId.ShouldBeNull("user on an open or reopen is whoever opened it, not someone who pushed code");
    }

    [Theory]
    [InlineData(0, RepositoryVisibility.Private)]
    [InlineData(10, RepositoryVisibility.Internal)]
    [InlineData(20, RepositoryVisibility.Public)]
    [InlineData(99, null)]
    public void Every_gitlab_visibility_level_maps_to_one_visibility(int level, RepositoryVisibility? expected)
    {
        Normalize<PullRequestOpenedEvent>(Body("open", sourceProjectId: 15, visibilityLevel: level)).Origin.RepositoryVisibility.ShouldBe(expected);
    }

    [Fact]
    public void A_payload_without_origin_fields_knows_nothing_and_is_not_called_a_fork()
    {
        const string body = """{"object_kind":"merge_request","user":{"id":1,"username":"alice"},"object_attributes":{"id":99,"iid":5,"title":"t","source_branch":"f","target_branch":"main","action":"open","url":"https://x"}}""";

        var opened = Normalize<PullRequestOpenedEvent>(body);

        opened.Origin.ShouldBe(new PullRequestOrigin());
    }

    private T Normalize<T>(string body) where T : class
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Event"] = "Merge Request Hook", ["X-Gitlab-Event-UUID"] = "u-1" };

        return _normalizer.Normalize(_repositoryId, body, headers).ShouldBeOfType<T>();
    }

    private static string Body(string action, int sourceProjectId, int visibilityLevel, string oldrev = "null") =>
        $$"""
        {"object_kind":"merge_request","user":{"id":7,"username":"maintainer"},
         "project":{"id":15,"path_with_namespace":"acme/api","visibility_level":{{visibilityLevel}}},
         "object_attributes":{"id":99,"iid":5,"author_id":51,"source_project_id":{{sourceProjectId}},"target_project_id":15,
           "source":{"path_with_namespace":"{{(sourceProjectId == 15 ? "acme/api" : "evil/api")}}"},"target":{"path_with_namespace":"acme/api"},
           "title":"t","source_branch":"main","target_branch":"main","action":"{{action}}","url":"https://x","oldrev":{{oldrev}},"last_commit":{"id":"def"} },
         "labels":[]}
        """;
}
