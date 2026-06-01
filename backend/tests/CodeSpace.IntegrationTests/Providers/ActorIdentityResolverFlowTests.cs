using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// Pins the actor-identity resolution contract (Model B): a user's linked identity resolves only when
/// it is live AND its credential is usable. A missing link, a link on another instance, a soft-deleted
/// link, or a revoked / soft-deleted credential all resolve to null — identical to "never linked" — so
/// the caller's enforcement (warn-fallback / strict) has one unambiguous signal.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ActorIdentityResolverFlowTests
{
    private readonly PostgresFixture _fixture;

    public ActorIdentityResolverFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Resolves_a_linked_identity_with_an_active_credential()
    {
        var (userId, instanceId, credId) = await SeedBaseAsync().ConfigureAwait(false);
        await LinkIdentityAsync(userId, instanceId, credId).ConfigureAwait(false);

        var resolved = await ResolveAsync(userId, instanceId).ConfigureAwait(false);

        resolved.ShouldNotBeNull();
        resolved!.UserId.ShouldBe(userId);
        resolved.ProviderInstanceId.ShouldBe(instanceId);
        resolved.CredentialId.ShouldBe(credId);
        resolved.ProviderUsername.ShouldBe("alice");
        resolved.ProviderUserId.ShouldBe("12345");
        // The credential (the token-bearer) is loaded so the caller can authenticate as this identity.
        resolved.Credential.ShouldNotBeNull();
        resolved.Credential.Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task Returns_null_when_the_user_has_no_linked_identity()
    {
        var (userId, instanceId, _) = await SeedBaseAsync().ConfigureAwait(false);

        var resolved = await ResolveAsync(userId, instanceId).ConfigureAwait(false);

        resolved.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_for_a_different_provider_instance()
    {
        var (userId, instanceId, credId) = await SeedBaseAsync().ConfigureAwait(false);
        await LinkIdentityAsync(userId, instanceId, credId).ConfigureAwait(false);

        // The user linked an identity on `instanceId`, but a write targeting a different instance
        // must not borrow it — identities are per (user, instance).
        var resolved = await ResolveAsync(userId, Guid.NewGuid()).ConfigureAwait(false);

        resolved.ShouldBeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_identity_is_soft_deleted()
    {
        var (userId, instanceId, credId) = await SeedBaseAsync().ConfigureAwait(false);
        await LinkIdentityAsync(userId, instanceId, credId, deleted: true).ConfigureAwait(false);

        var resolved = await ResolveAsync(userId, instanceId).ConfigureAwait(false);

        resolved.ShouldBeNull();
    }

    [Theory]
    [InlineData(CredentialStatus.Revoked, false)]   // operator revoked the token
    [InlineData(CredentialStatus.Active, true)]      // credential soft-deleted out from under the identity
    public async Task Returns_null_when_the_linked_credential_is_not_usable(CredentialStatus credStatus, bool credentialDeleted)
    {
        var (userId, instanceId, credId) = await SeedBaseAsync(credStatus, credentialDeleted).ConfigureAwait(false);
        await LinkIdentityAsync(userId, instanceId, credId).ConfigureAwait(false);

        var resolved = await ResolveAsync(userId, instanceId).ConfigureAwait(false);

        resolved.ShouldBeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<UserProviderIdentity?> ResolveAsync(Guid userId, Guid instanceId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IActorIdentityResolver>().ResolveAsync(userId, instanceId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<(Guid UserId, Guid InstanceId, Guid CredId)> SeedBaseAsync(CredentialStatus credStatus = CredentialStatus.Active, bool credentialDeleted = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.GitHub,
            DisplayName = "instance",
            BaseUrl = $"https://github-{suffix}.local",
            OauthClientId = "client",
            OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var cred = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            OwnerUserId = user.Id,
            Ownership = CredentialOwnership.Personal,
            AuthType = AuthType.Pat,
            DisplayName = "actor-cred",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "glpat_xxx" })),
            Status = credStatus,
            DeletedDate = credentialDeleted ? DateTimeOffset.UtcNow : null
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(cred);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, instance.Id, cred.Id);
    }

    private async Task<Guid> LinkIdentityAsync(Guid userId, Guid instanceId, Guid credId, bool deleted = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var identity = new UserProviderIdentity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderInstanceId = instanceId,
            CredentialId = credId,
            ProviderUserId = "12345",
            ProviderUsername = "alice",
            AvatarUrl = "https://example.test/alice.png",
            DeletedDate = deleted ? DateTimeOffset.UtcNow : null
        };

        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return identity.Id;
    }
}
