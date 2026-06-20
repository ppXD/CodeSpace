using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Repositories;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// Coverage of the latest-release read path (<see cref="IRepositoryInsightsService.GetLatestReleaseAsync"/>)
/// on real Postgres via the test provider (<c>ProviderKind.Git</c>), which echoes the repo's full path into
/// the release name so the test asserts the repo identity reached the provider. Exercises the shared insights
/// preflight (repo lookup → credential null-check → scope → capability dispatch).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class InsightsReleaseFlowTests
{
    private readonly PostgresFixture _fixture;

    public InsightsReleaseFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GetLatestRelease_resolves_the_repo_and_returns_the_providers_release()
    {
        var seed = await SeedAsync();

        using var scope = _fixture.BeginScope();
        var release = await scope.Resolve<IRepositoryInsightsService>().GetLatestReleaseAsync(seed.RepositoryId, CancellationToken.None);

        release.ShouldNotBeNull();
        release!.TagName.ShouldBe("3.0.5");
        release.Name.ShouldBe($"Release for {seed.FullPath}",
            customMessage: "the repo identity must reach the provider so the right release comes back");
        release.IsPrerelease.ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_repository_is_refused()
    {
        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            scope.Resolve<IRepositoryInsightsService>().GetLatestReleaseAsync(Guid.NewGuid(), CancellationToken.None));
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

        return new SeedResult(repo.Id, repo.FullPath);
    }

    private sealed record SeedResult(Guid RepositoryId, string FullPath);
}
