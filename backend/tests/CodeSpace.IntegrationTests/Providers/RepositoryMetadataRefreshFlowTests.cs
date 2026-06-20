using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Repositories;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// Read-through metadata refresh: <see cref="IRepositoryService.GetAsync"/> re-syncs the stored repo
/// metadata from the provider before returning, so the detail header reflects live state (e.g. a repo
/// flipped private→public). The test provider's <c>GetByExternalIdAsync</c> always reports the repo as
/// Private on <c>main</c>, so a repo seeded as Public on a stale branch must come back refreshed + persisted.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositoryMetadataRefreshFlowTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryMetadataRefreshFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GetAsync_refreshes_stale_metadata_from_the_provider_and_persists_it()
    {
        var seed = await SeedAsync(withCredential: true, visibility: RepositoryVisibility.Public, defaultBranch: "stale-branch");

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IRepositoryService>().GetAsync(seed.RepositoryId, CancellationToken.None);

        detail.ShouldNotBeNull();
        detail!.Visibility.ShouldBe(RepositoryVisibility.Private, customMessage: "stale Public must be refreshed to the provider's live Private");
        detail.DefaultBranch.ShouldBe("main", customMessage: "stale default branch must be refreshed");
        detail.LastSyncedDate.ShouldNotBeNull();

        // Persisted, not just projected — a fresh read sees the refreshed value.
        using var verify = _fixture.BeginScope();
        var stored = await verify.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().SingleAsync(r => r.Id == seed.RepositoryId);
        stored.Visibility.ShouldBe(RepositoryVisibility.Private);
    }

    [Fact]
    public async Task GetAsync_serves_stored_values_when_the_repo_has_no_credential()
    {
        // No credential → nothing live to read; the refresh no-ops and the detail renders from stored state.
        var seed = await SeedAsync(withCredential: false, visibility: RepositoryVisibility.Public, defaultBranch: "dev");

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IRepositoryService>().GetAsync(seed.RepositoryId, CancellationToken.None);

        detail.ShouldNotBeNull();
        detail!.Visibility.ShouldBe(RepositoryVisibility.Public, customMessage: "no credential → keep stored, don't error");
        detail.DefaultBranch.ShouldBe("dev");
    }

    private async Task<SeedResult> SeedAsync(bool withCredential, RepositoryVisibility visibility, string defaultBranch)
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
        Credential? connection = withCredential
            ? new Credential
            {
                Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
                AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = Pat("conn"), Status = CredentialStatus.Active
            }
            : null;
        var repo = new Repository
        {
            // ExternalId is path-like so the test provider's GetByExternalIdAsync derives acme/api on `main`.
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = connection?.Id,
            ExternalId = "acme/api", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = defaultBranch, Visibility = visibility, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        if (connection != null) db.Credential.Add(connection);
        db.Repository.Add(repo);

        await db.SaveChangesAsync();

        return new SeedResult(repo.Id);
    }

    private sealed record SeedResult(Guid RepositoryId);
}
