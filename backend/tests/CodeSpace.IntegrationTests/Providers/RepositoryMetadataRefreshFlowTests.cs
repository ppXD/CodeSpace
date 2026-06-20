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
/// Stale-while-revalidate metadata refresh: <see cref="IRepositoryService.GetAsync"/> serves the stored
/// snapshot instantly by default, and ONLY re-syncs from the provider when called with refresh=true (the
/// background read the detail page issues after its instant paint). The test provider's
/// <c>GetByExternalIdAsync</c> always reports the repo as Private on <c>main</c>, so a repo seeded as Public
/// on a stale branch comes back refreshed+persisted with refresh=true, but untouched with refresh=false.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositoryMetadataRefreshFlowTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryMetadataRefreshFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GetAsync_with_refresh_resyncs_stale_metadata_from_the_provider_and_persists_it()
    {
        var seed = await SeedAsync(withCredential: true, visibility: RepositoryVisibility.Public, defaultBranch: "stale-branch");

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IRepositoryService>().GetAsync(seed.RepositoryId, refresh: true, CancellationToken.None);

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
    public async Task GetAsync_with_refresh_but_no_credential_keeps_stored_values()
    {
        // No credential → nothing live to read; the refresh no-ops and the detail renders from stored state.
        var seed = await SeedAsync(withCredential: false, visibility: RepositoryVisibility.Public, defaultBranch: "dev");

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IRepositoryService>().GetAsync(seed.RepositoryId, refresh: true, CancellationToken.None);

        detail.ShouldNotBeNull();
        detail!.Visibility.ShouldBe(RepositoryVisibility.Public, customMessage: "no credential → keep stored, don't error");
        detail.DefaultBranch.ShouldBe("dev");
    }

    [Fact]
    public async Task GetAsync_without_refresh_serves_the_stored_snapshot_and_skips_the_provider()
    {
        // The default (refresh=false) read is the instant path the detail page paints from — it must NOT pay
        // the provider round-trip. Seeded stale Public on `stale-branch` stays exactly that, and the row is
        // never re-synced (LastSyncedDate stays null), proving the provider was not called + nothing persisted.
        var seed = await SeedAsync(withCredential: true, visibility: RepositoryVisibility.Public, defaultBranch: "stale-branch");

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IRepositoryService>().GetAsync(seed.RepositoryId, refresh: false, CancellationToken.None);

        detail.ShouldNotBeNull();
        detail!.Visibility.ShouldBe(RepositoryVisibility.Public, customMessage: "no refresh → serve the stored snapshot unchanged");
        detail.DefaultBranch.ShouldBe("stale-branch");
        detail.LastSyncedDate.ShouldBeNull(customMessage: "refresh=false must not re-sync, so LastSyncedDate stays unstamped");

        // And nothing was persisted — a fresh read still sees the stale seed values.
        using var verify = _fixture.BeginScope();
        var stored = await verify.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().SingleAsync(r => r.Id == seed.RepositoryId);
        stored.Visibility.ShouldBe(RepositoryVisibility.Public);
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
