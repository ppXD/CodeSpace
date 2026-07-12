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
/// Pins the act-as candidate list that feeds the actAsUserId picker: it offers a teammate ONLY when
/// acting as them would actually succeed — i.e. they have a live, usable identity on the repo's provider
/// instance. Uses the SAME predicate as <see cref="IActorIdentityResolver.ResolveAsync"/>, so the key
/// invariant is that every candidate returned resolves (never throws ActorIdentityRequiredException).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ActAsCandidatesFlowTests
{
    private readonly PostgresFixture _fixture;

    public ActAsCandidatesFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Lists_a_teammate_with_a_live_identity_with_display_fields()
    {
        var ctx = await SeedAsync().ConfigureAwait(false);
        await LinkIdentityAsync(ctx, ctx.OwnerUserId, "alice", "12345").ConfigureAwait(false);

        var candidates = await ListAsync(ctx.RepositoryId, ctx.TeamId).ConfigureAwait(false);

        candidates.Count.ShouldBe(1);
        var c = candidates[0];
        c.UserId.ShouldBe(ctx.OwnerUserId);
        c.Name.ShouldBe("owner");
        c.ProviderUsername.ShouldBe("alice");
        c.ProviderUserId.ShouldBe("12345");
    }

    [Fact]
    public async Task Every_returned_candidate_resolves_without_throwing()
    {
        // The load-bearing invariant: the picker must never offer a user whose act-as write would throw.
        var ctx = await SeedAsync().ConfigureAwait(false);
        await LinkIdentityAsync(ctx, ctx.OwnerUserId, "alice", "12345").ConfigureAwait(false);

        var candidates = await ListAsync(ctx.RepositoryId, ctx.TeamId).ConfigureAwait(false);

        candidates.ShouldNotBeEmpty();
        foreach (var c in candidates)
        {
            var resolved = await ResolveAsync(c.UserId, ctx.ProviderInstanceId).ConfigureAwait(false);
            resolved.ShouldNotBeNull();   // guaranteed usable at write time — no ActorIdentityRequiredException
        }
    }

    [Theory]
    [InlineData(CredentialStatus.Revoked, false, false)]   // operator revoked the token
    [InlineData(CredentialStatus.Active, true, false)]      // credential soft-deleted under the identity
    [InlineData(CredentialStatus.Active, false, true)]      // identity itself soft-deleted
    public async Task Excludes_a_user_whose_identity_or_credential_is_unusable(CredentialStatus credStatus, bool credentialDeleted, bool identityDeleted)
    {
        var ctx = await SeedAsync().ConfigureAwait(false);
        await LinkIdentityAsync(ctx, ctx.OwnerUserId, "alice", "12345", credStatus, credentialDeleted, identityDeleted).ConfigureAwait(false);

        var candidates = await ListAsync(ctx.RepositoryId, ctx.TeamId).ConfigureAwait(false);

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Excludes_a_user_who_is_not_a_team_member()
    {
        var ctx = await SeedAsync().ConfigureAwait(false);
        // A stranger has a live identity on the SAME provider instance but is NOT in the team — must not appear.
        var stranger = await AddUserAsync("stranger").ConfigureAwait(false);
        await LinkIdentityAsync(ctx, stranger, "mallory", "999").ConfigureAwait(false);

        var candidates = await ListAsync(ctx.RepositoryId, ctx.TeamId).ConfigureAwait(false);

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Empty_when_the_repository_is_not_the_teams()
    {
        var ctx = await SeedAsync().ConfigureAwait(false);
        await LinkIdentityAsync(ctx, ctx.OwnerUserId, "alice", "12345").ConfigureAwait(false);

        var candidates = await ListAsync(Guid.NewGuid(), ctx.TeamId).ConfigureAwait(false);

        candidates.ShouldBeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed record SeedCtx(Guid TeamId, Guid OwnerUserId, Guid ProviderInstanceId, Guid RepositoryId);

    private async Task<IReadOnlyList<Messages.Dtos.Providers.ActAsCandidateSummary>> ListAsync(Guid repositoryId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IActorIdentityResolver>().ListCandidatesAsync(repositoryId, teamId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<UserProviderIdentity?> ResolveAsync(Guid userId, Guid instanceId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IActorIdentityResolver>().ResolveAsync(userId, instanceId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<SeedCtx> SeedAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = owner.Id };
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
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            ExternalId = $"ext-{suffix}",
            NamespacePath = "acme",
            Name = "api",
            FullPath = $"acme/api-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://git.local/acme/api",
            Status = RepositoryStatus.Active
        };

        db.User.Add(owner);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Repository.Add(repo);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeedCtx(team.Id, owner.Id, instance.Id, repo.Id);
    }

    private async Task<Guid> AddUserAsync(string name)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"{name}-{Guid.NewGuid():N}@x", Name = name };
        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return user.Id;
    }

    private async Task LinkIdentityAsync(SeedCtx ctx, Guid userId, string username, string providerUserId,
        CredentialStatus credStatus = CredentialStatus.Active, bool credentialDeleted = false, bool identityDeleted = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var cred = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = ctx.TeamId,
            ProviderInstanceId = ctx.ProviderInstanceId,
            OwnerUserId = userId,
            Ownership = CredentialOwnership.Personal,
            AuthType = AuthType.Pat,
            DisplayName = "actor-cred",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "glpat_xxx" })),
            Status = credStatus,
            DeletedDate = credentialDeleted ? DateTimeOffset.UtcNow : null
        };
        var identity = new UserProviderIdentity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProviderInstanceId = ctx.ProviderInstanceId,
            CredentialId = cred.Id,
            ProviderUserId = providerUserId,
            ProviderUsername = username,
            AvatarUrl = "https://example.test/a.png",
            DeletedDate = identityDeleted ? DateTimeOffset.UtcNow : null
        };

        db.Credential.Add(cred);
        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
