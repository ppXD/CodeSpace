using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// A pull-request comment through the whole stack — <see cref="IPullRequestService"/>, the provider registry, the
/// real GitHub provider, the container's singleton resilience wrapper, Octokit, the wire — against a loopback
/// GitHub that saves the comment and then answers 502, the way a gateway does when the backend committed but the
/// answer did not make it back. The retry has to adopt the saved comment: one POST, one comment on the pull
/// request, and that comment is what the service returns.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProviderWriteRetryFlowTests
{
    private readonly PostgresFixture _fixture;

    public ProviderWriteRetryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_comment_GitHub_saved_before_answering_502_is_adopted_not_posted_again()
    {
        var saved = new List<string>();

        using var github = new StubProviderHost()
            .Answer("POST", "/repos/acme/api/issues/7/comments", request => SaveThenFailTheFirst(saved, request))
            .Answer("GET", "/repos/acme/api/issues/7/comments", _ => new StubReply(200, "[" + string.Join(",", saved.Select((body, i) => CommentJson(i + 1, body))) + "]"));

        var seed = await SeedGitHubRepositoryAsync(github.BaseUrl);

        using var scope = _fixture.BeginScope();
        var comment = await scope.Resolve<IPullRequestService>().PostCommentAsync(seed.RepositoryId, seed.TeamId, 7, "Looks good.", CancellationToken.None);

        saved.ShouldHaveSingleItem("GitHub saved the comment before the 502 — posting it again would show it twice on the pull request").ShouldStartWith("Looks good.\n\n<!-- codespace:idempotency:");
        comment.ExternalId.ShouldBe("1", customMessage: "the service must hand back the comment that landed, found by its marker");
        comment.Body.ShouldBe("Looks good.", customMessage: "CodeSpace shows what was written; the marker lives only in GitHub's copy");
        github.Requests.Count(r => r.Method == "POST").ShouldBe(1, customMessage: "after an ambiguous failure the provider probes; it must not re-send a create the probe found");
    }

    private static StubReply SaveThenFailTheFirst(List<string> saved, RecordedRequest request)
    {
        saved.Add(JsonDocument.Parse(request.Body).RootElement.GetProperty("body").GetString()!);

        return saved.Count == 1 ? new StubReply(502, """{"message":"Bad Gateway"}""") : new StubReply(201, CommentJson(saved.Count, saved[^1]));
    }

    private static string CommentJson(long id, string body) => JsonSerializer.Serialize(new { id, body, user = new { login = "codespace-bot" }, created_at = "2026-09-24T08:00:00Z", html_url = $"https://github.test/acme/api/pull/7#issuecomment-{id}" });

    private async Task<SeedResult> SeedGitHubRepositoryAsync(string baseUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitHub, DisplayName = "loopback", BaseUrl = baseUrl, ApiUrl = baseUrl };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "ghp_loopback" })), Status = CredentialStatus.Active
        };
        var repository = new Repository
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id,
            ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://github.test/acme/api", Status = RepositoryStatus.Active
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeedResult(team.Id, repository.Id);
    }

    private sealed record SeedResult(Guid TeamId, Guid RepositoryId);
}
