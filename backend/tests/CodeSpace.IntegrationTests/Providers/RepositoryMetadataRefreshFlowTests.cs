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

    [Fact]
    public async Task A_refresh_that_moves_a_connection_repository_out_from_under_its_hook_stages_and_dispatches_a_hook_for_the_new_owner()
    {
        // The group was renamed (or the project transferred) at the provider. The hook row keeps the owner path it was
        // registered under, so once the refresh rewrites the repository's NamespacePath no hook covers it and every
        // delivery for it is refused. The refresh is the moment the move is learned, so it is where coverage is restored.
        var seed = await SeedConnectionAsync(ProviderWebhookScope.Connection, storedOwner: "acme-{s}", providerOwner: "acme-corp-{s}", hookOwner: "acme-{s}");

        await RefreshAsync(seed.RepositoryId);

        var hooks = await LoadConnectionHooksAsync(seed.InstanceId);
        var covering = hooks.SingleOrDefault(h => h.OwnerPath == seed.ProviderOwner);
        covering.ShouldNotBeNull($"no hook covers the repository's new owner '{seed.ProviderOwner}', so every delivery for it would be refused. Hooks: {string.Join(", ", hooks.Select(h => h.OwnerPath))}");
        covering!.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued, "the staged hook must be dispatched once the refresh commits, or it sits Pending — nothing sweeps connection hooks.");
        covering.CredentialId.ShouldBe(seed.CredentialId);
        hooks.ShouldContain(h => h.OwnerPath == seed.HookOwner, "the old hook is left alone: a rename keeps it delivering for repositories not yet refreshed.");
    }

    [Theory]
    [InlineData(ProviderWebhookScope.Connection, "acme-{s}", "acme-{s}", "acme-{s}")]                  // nothing moved
    [InlineData(ProviderWebhookScope.Connection, "other-{s}", "acme-{s}/platform", "acme-{s}")]        // moved under an owner a hook already covers
    [InlineData(ProviderWebhookScope.Repository, "acme-{s}", "acme-corp-{s}", null)]                    // per-repository scope: its own hook follows it
    public async Task A_refresh_that_leaves_the_repository_covered_stages_no_hook(ProviderWebhookScope scope, string storedOwner, string providerOwner, string? hookOwner)
    {
        // A repository page view is what triggers this, so anything short of "the move left it uncovered" must write no hook.
        var seed = await SeedConnectionAsync(scope, storedOwner, providerOwner, hookOwner);
        var before = (await LoadConnectionHooksAsync(seed.InstanceId)).Count;

        await RefreshAsync(seed.RepositoryId);

        (await LoadConnectionHooksAsync(seed.InstanceId)).Count.ShouldBe(before, "a refresh that leaves the repository covered must not stage a connection hook.");
        (await LoadNamespacePathAsync(seed.RepositoryId)).ShouldBe(seed.ProviderOwner, "the refresh itself must still apply the provider's placement.");
    }

    private async Task RefreshAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IRepositoryService>().GetAsync(repositoryId, refresh: true, CancellationToken.None);
    }

    private async Task<List<ConnectionWebhook>> LoadConnectionHooksAsync(Guid instanceId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().Where(w => w.ProviderInstanceId == instanceId).ToListAsync();
    }

    private async Task<string> LoadNamespacePathAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().Where(r => r.Id == repositoryId).Select(r => r.NamespacePath).SingleAsync();
    }

    /// <summary>
    /// A repository stored under <paramref name="storedOwner"/> whose provider now reports it under
    /// <paramref name="providerOwner"/> (the test provider derives placement from the path-like ExternalId), plus a
    /// Registered connection hook on <paramref name="hookOwner"/> when one is given. <c>{s}</c> is replaced by a
    /// per-test suffix so owners never collide on the connection-hook owner index.
    /// </summary>
    private async Task<ConnectionSeed> SeedConnectionAsync(ProviderWebhookScope scope, string storedOwner, string providerOwner, string? hookOwner)
    {
        using var lifetime = _fixture.BeginScope();
        var db = lifetime.Resolve<CodeSpaceDbContext>();
        var encryptor = lifetime.Resolve<IPayloadEncryptor>();
        var serializer = lifetime.Resolve<ICredentialPayloadSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string Owner(string template) => template.Replace("{s}", suffix);

        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "instance", BaseUrl = $"https://git-{suffix}.local", WebhookScope = scope };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "conn" })), Status = CredentialStatus.Active };
        var repo = new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = $"{Owner(providerOwner)}/api", NamespacePath = Owner(storedOwner), Name = "api", FullPath = $"{Owner(storedOwner)}/api", WebUrl = "https://git.local/api", Status = RepositoryStatus.Active };

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repo);

        if (hookOwner != null)
            db.ConnectionWebhook.Add(new ConnectionWebhook { Id = Guid.NewGuid(), ProviderInstanceId = instance.Id, CredentialId = credential.Id, OwnerPath = Owner(hookOwner), ExternalId = "g1", CallbackUrl = $"https://codespace.test/c/{suffix}", SecretEnc = encryptor.Encrypt("s"), SubscribedEvents = new List<string> { "push" }, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered, RegisteredAt = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync();

        return new ConnectionSeed(instance.Id, credential.Id, repo.Id, Owner(providerOwner), hookOwner == null ? null : Owner(hookOwner));
    }

    private sealed record ConnectionSeed(Guid InstanceId, Guid CredentialId, Guid RepositoryId, string ProviderOwner, string? HookOwner);

    private async Task<SeedResult> SeedAsync(bool withCredential, RepositoryVisibility visibility, string defaultBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
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
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        if (connection != null) db.Credential.Add(connection);
        db.Repository.Add(repo);

        await db.SaveChangesAsync();

        return new SeedResult(repo.Id);
    }

    private sealed record SeedResult(Guid RepositoryId);
}
