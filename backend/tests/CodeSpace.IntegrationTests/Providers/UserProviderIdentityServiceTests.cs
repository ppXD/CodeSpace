using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// Pins the Model B PAT-linking contract on real Postgres, with the provider's whoami (the generic
/// <see cref="ICredentialProbeCapability"/>) stubbed so no real GitLab/GitHub is hit. The token is
/// probed before anything persists:
///   • valid   → a Personal credential + identity are stored, carrying the provider-side profile;
///   • invalid → throws, and NO rows are written (no orphan credential);
///   • re-link → the old link + its credential are soft-deleted, exactly one stays live;
///   • unlink  → identity soft-deleted + credential token material cleared;
///   • list    → scoped to the caller (you never see another user's identities).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class UserProviderIdentityServiceTests
{
    private readonly PostgresFixture _fixture;

    public UserProviderIdentityServiceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Link_with_a_valid_token_stores_the_credential_and_identity_with_the_profile()
    {
        var (userId, teamId, instanceId) = await SeedAsync();
        var probe = ValidProbe("99", "alice");

        UserProviderIdentitySummary summary;
        using (var scope = ScopeAs(userId, teamId, probe))
            summary = await scope.Resolve<IUserProviderIdentityService>().LinkByPatAsync(instanceId, "glpat-xyz", CancellationToken.None);

        summary.ProviderUsername.ShouldBe("alice");
        summary.ProviderUserId.ShouldBe("99");
        summary.CredentialStatus.ShouldBe(CredentialStatus.Active);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var identity = await db.UserProviderIdentity.AsNoTracking().Include(i => i.Credential)
            .SingleAsync(i => i.UserId == userId && i.ProviderInstanceId == instanceId && i.DeletedDate == null);
        identity.ProviderUsername.ShouldBe("alice");
        identity.Credential.OwnerUserId.ShouldBe(userId);
        identity.Credential.Ownership.ShouldBe(CredentialOwnership.Personal);
        identity.Credential.AuthType.ShouldBe(AuthType.Pat);
        identity.Credential.EncryptedPayload.ShouldNotBeNullOrEmpty("the PAT must be stored encrypted, not blank");
    }

    [Fact]
    public async Task Link_with_an_invalid_token_throws_and_writes_no_rows()
    {
        var (userId, teamId, instanceId) = await SeedAsync();
        var probe = new StubProbe { Result = new CredentialProbeResult { IsValid = false, Error = "401 Unauthorized" } };

        using (var scope = ScopeAs(userId, teamId, probe))
        {
            var service = scope.Resolve<IUserProviderIdentityService>();
            await Should.ThrowAsync<InvalidOperationException>(() => service.LinkByPatAsync(instanceId, "bad-token", CancellationToken.None));
        }

        // The transient credential is probed BEFORE persistence — a rejected token must leave nothing behind.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.UserProviderIdentity.AsNoTracking().CountAsync(i => i.UserId == userId)).ShouldBe(0, "no identity row for a rejected token");
        (await db.Credential.AsNoTracking().CountAsync(c => c.OwnerUserId == userId)).ShouldBe(0, "no orphan credential for a rejected token");
    }

    [Fact]
    public async Task Re_link_replaces_the_existing_identity_in_place()
    {
        var (userId, teamId, instanceId) = await SeedAsync();

        using (var scope = ScopeAs(userId, teamId, ValidProbe("1", "old")))
            await scope.Resolve<IUserProviderIdentityService>().LinkByPatAsync(instanceId, "tok-1", CancellationToken.None);
        using (var scope = ScopeAs(userId, teamId, ValidProbe("2", "new")))
            await scope.Resolve<IUserProviderIdentityService>().LinkByPatAsync(instanceId, "tok-2", CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var live = await db.UserProviderIdentity.AsNoTracking().Where(i => i.UserId == userId && i.DeletedDate == null).ToListAsync();
        live.Count.ShouldBe(1, "exactly one live identity per (user, instance) — the re-link replaced the old one");
        live[0].ProviderUsername.ShouldBe("new");

        // The superseded credential is soft-deleted + its token cleared.
        var supersededCreds = await db.Credential.AsNoTracking().Where(c => c.OwnerUserId == userId && c.DeletedDate != null).ToListAsync();
        supersededCreds.ShouldAllBe(c => c.EncryptedPayload == string.Empty);
    }

    [Fact]
    public async Task Unlink_soft_deletes_the_identity_and_clears_the_credential_token()
    {
        var (userId, teamId, instanceId) = await SeedAsync();

        Guid identityId;
        using (var scope = ScopeAs(userId, teamId, ValidProbe("7", "carol")))
            identityId = (await scope.Resolve<IUserProviderIdentityService>().LinkByPatAsync(instanceId, "tok", CancellationToken.None)).Id;

        using (var scope = ScopeAs(userId, teamId, ValidProbe("7", "carol")))
            await scope.Resolve<IUserProviderIdentityService>().UnlinkAsync(identityId, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var identity = await db.UserProviderIdentity.AsNoTracking().Include(i => i.Credential).SingleAsync(i => i.Id == identityId);
        identity.DeletedDate.ShouldNotBeNull();
        identity.Credential.DeletedDate.ShouldNotBeNull();
        identity.Credential.EncryptedPayload.ShouldBe(string.Empty, "unlink must leave no usable token at rest");
    }

    [Fact]
    public async Task List_is_scoped_to_the_calling_user()
    {
        var (alice, teamId, instanceId) = await SeedAsync();
        var bob = await SeedUserAsync(teamId);

        using (var scope = ScopeAs(alice, teamId, ValidProbe("1", "alice")))
            await scope.Resolve<IUserProviderIdentityService>().LinkByPatAsync(instanceId, "tok", CancellationToken.None);

        // Bob lists his own identities — Alice's link must not leak across.
        using var bobScope = ScopeAs(bob, teamId, ValidProbe("x", "x"));
        var bobIdentities = await bobScope.Resolve<IUserProviderIdentityService>().ListMineAsync(CancellationToken.None);
        bobIdentities.ShouldBeEmpty("a user only sees their own linked identities");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private ILifetimeScope ScopeAs(Guid userId, Guid teamId, ICredentialProbeCapability probe) =>
        _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "tester")).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            b.RegisterInstance<IProviderRegistry>(new StubProbeRegistry(probe)).SingleInstance();
        });

    private static StubProbe ValidProbe(string externalId, string username) =>
        new() { Result = new CredentialProbeResult { IsValid = true, AuthenticatedUserExternalId = externalId, AuthenticatedUserName = username } };

    private async Task<(Guid UserId, Guid TeamId, Guid InstanceId)> SeedAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.Git,
            DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local",
            OauthClientId = "client",
            OauthClientSecretEnc = encryptor.Encrypt("secret")
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return (user.Id, team.Id, instance.Id);
    }

    private async Task<Guid> SeedUserAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{Guid.NewGuid():N}@x", Name = "other" };
        db.User.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// A configurable probe. Must be parameterless-constructible: the fixture's IProviderCapability
    /// assembly-scan activates every capability in the test assembly into the real ProviderRegistry,
    /// so a ctor arg would crash registry construction. The default mirrors TestRepositoryProvider's
    /// canned valid probe (same Kind=Git) — the duplicate registration is harmless. Tests needing a
    /// specific result set <see cref="Result"/> and inject it via <see cref="StubProbeRegistry"/>,
    /// bypassing DI entirely.
    /// </summary>
    private sealed class StubProbe : ICredentialProbeCapability
    {
        public CredentialProbeResult Result { get; init; } = new() { IsValid = true, AuthenticatedUserExternalId = "test-user-id", AuthenticatedUserName = "Test User" };
        public ProviderKind Kind => ProviderKind.Git;
        public Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(Result);
    }

    /// <summary>Minimal registry that returns the stub probe for any kind; other lookups are unused here.</summary>
    private sealed class StubProbeRegistry : IProviderRegistry
    {
        private readonly ICredentialProbeCapability _probe;
        public StubProbeRegistry(ICredentialProbeCapability probe) { _probe = probe; }

        public TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability => (TCapability)(object)_probe;

        public bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability
        {
            capability = _probe as TCapability;
            return capability != null;
        }

        public IReadOnlyList<Type> GetCapabilities(ProviderKind kind) => Array.Empty<Type>();
    }
}
