using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Core.Services.ReleaseCatalog;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// End-to-end coverage of the in-app issue-detail + release-catalog read paths on real Postgres via the
/// test provider (<c>ProviderKind.Git</c>), which echoes the number / page into its canned results so the
/// test asserts they flowed through the shared read preflight (repo lookup → scope → capability dispatch).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class IssueReleaseDetailFlowTests
{
    private readonly PostgresFixture _fixture;

    public IssueReleaseDetailFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GetIssue_forwards_the_number_and_returns_detail_fields()
    {
        var seed = await SeedAsync();

        using var scope = _fixture.BeginScope();
        var issue = await scope.Resolve<IIssueService>().GetAsync(seed.RepositoryId, seed.TeamId, 42, CancellationToken.None);

        issue.Number.ShouldBe(42, customMessage: "the per-repo number must reach the provider");
        issue.Assignees.ShouldContain("mindy");
        issue.Body.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ListComments_and_events_resolve()
    {
        var seed = await SeedAsync();

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IIssueService>();
        var comments = await svc.ListCommentsAsync(seed.RepositoryId, seed.TeamId, 42, CancellationToken.None);
        var events = await svc.ListEventsAsync(seed.RepositoryId, seed.TeamId, 42, CancellationToken.None);

        comments.ShouldHaveSingleItem().AuthorName.ShouldBe("tester");
        events.ShouldHaveSingleItem().Kind.ShouldBe("closed");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task ListReleases_forwards_paging_and_marks_latest_only_on_page_one(int page, bool expectLatest)
    {
        var seed = await SeedAsync();

        using var scope = _fixture.BeginScope();
        var releases = await scope.Resolve<IReleaseCatalogService>().ListReleasesAsync(seed.RepositoryId, page, perPage: 30, CancellationToken.None);

        var r = releases.ShouldHaveSingleItem();
        r.TagName.ShouldBe($"3.0.{page}", customMessage: "page must reach the provider");
        r.IsLatest.ShouldBe(expectLatest);
        r.Assets.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ListTags_resolves()
    {
        var seed = await SeedAsync();

        using var scope = _fixture.BeginScope();
        var tags = await scope.Resolve<IReleaseCatalogService>().ListTagsAsync(seed.RepositoryId, page: 1, perPage: 30, CancellationToken.None);

        tags.ShouldHaveSingleItem().Name.ShouldBe("3.0.1");
    }

    private async Task<SeedResult> SeedAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var connection = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = Pat("conn"), Status = CredentialStatus.Active
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = connection.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);
        db.Repository.Add(repo);

        await db.SaveChangesAsync();

        return new SeedResult(team.Id, repo.Id);
    }

    private sealed record SeedResult(Guid TeamId, Guid RepositoryId);
}
