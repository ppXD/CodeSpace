using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Identity;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// The people, provider and credential flows the binding-move tests share: a team with one person of each role, one
/// provider instance, and the same doors a person uses to hold a connection — the Providers modal's "Use a token",
/// "Connect with OAuth" and "Disconnect" — driven through the real mediator pipeline, so the authorization behaviors,
/// the unit of work and the post-commit drain are the production ones. Only the provider's own edges are the test
/// doubles the fixture already registers (the Git test provider's whoami and catalog, <see cref="StubOAuthClient"/>'s
/// token endpoint).
/// </summary>
internal sealed class BindingMoveKit
{
    private readonly PostgresFixture _fixture;

    public BindingMoveKit(PostgresFixture fixture) { _fixture = fixture; }

    public sealed record World(Guid TeamId, Guid InstanceId, Guid OwnerId, Guid AdminId, Guid MemberId, Guid ViewerId, StubOAuthClient OAuth);

    public PostgresFixture Fixture => _fixture;

    public async Task<World> SeedWorldAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var users = new[] { "owner", "admin", "member", "viewer" }.Select(label => new User { Id = Guid.NewGuid(), Email = $"{label}-{suffix}@x", Name = $"{label}-{suffix}" }).ToArray();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = NewInstance(team.Id, suffix, scope.Resolve<IPayloadEncryptor>());

        db.Team.Add(team);
        db.User.AddRange(users);
        db.TeamMembership.AddRange(Membership(team, users[0], TeamRole.Owner), Membership(team, users[1], TeamRole.Admin), Membership(team, users[2], TeamRole.Member), Membership(team, users[3], TeamRole.Viewer));
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return new World(team.Id, instance.Id, users[0].Id, users[1].Id, users[2].Id, users[3].Id, new StubOAuthClient(ProviderKind.GitLab, Token("unused", null)));
    }

    /// <summary>A second provider instance of the same team — a repository and a credential on different instances can never serve each other.</summary>
    public async Task<Guid> SeedOtherInstanceAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = NewInstance(world.TeamId, Guid.NewGuid().ToString("N")[..8], scope.Resolve<IPayloadEncryptor>());

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return instance.Id;
    }

    /// <summary>
    /// A second connection of the world's team to the SAME host as <paramref name="ofInstanceId"/> (the world's own by default) —
    /// another OAuth application on one GitLab: same provider and base URL, its own client id, which is what the unique index lets
    /// a team hold twice. Credentials and repositories on the two can serve each other, since the repositories behind them are the same.
    /// </summary>
    public async Task<Guid> SeedSiblingInstanceAsync(World world, Guid? ofInstanceId = null, ProviderWebhookScope webhookScope = ProviderWebhookScope.Repository, string? apiUrl = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var of = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == (ofInstanceId ?? world.InstanceId));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var sibling = new ProviderInstance { Id = Guid.NewGuid(), TeamId = of.TeamId, Provider = of.Provider, DisplayName = $"GitLab sibling {suffix}", BaseUrl = of.BaseUrl, ApiUrl = apiUrl ?? of.ApiUrl, WebhookScope = webhookScope, OauthClientId = $"client-{suffix}", OauthClientSecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("client-secret") };

        db.ProviderInstance.Add(sibling);
        await db.SaveChangesAsync();

        return sibling.Id;
    }

    /// <summary>The connection moved to <paramref name="webhookScope"/> as an operator's edit leaves it, with nothing registered yet to retire — for a test that needs a connection-wide target.</summary>
    public async Task SetWebhookScopeAsync(Guid instanceId, ProviderWebhookScope webhookScope)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().ProviderInstance.Where(p => p.Id == instanceId).ExecuteUpdateAsync(s => s.SetProperty(p => p.WebhookScope, webhookScope));
    }

    /// <summary>A person with a membership in the world's team at <paramref name="role"/> — the shape a later joiner or a leaver takes.</summary>
    public async Task<Guid> SeedMemberAsync(World world, TeamRole role)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"{role}-{suffix}@x", Name = $"{role}-{suffix}" };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.User.Add(user);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = world.TeamId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync();

        return user.Id;
    }

    /// <summary>
    /// A scope for one request as <paramref name="userId"/> (anonymous when null). The provider is a GitLab instance, so the real GitLab auth
    /// strategies read every credential; its API answers come from the Git test provider, and its token endpoint from
    /// <see cref="StubOAuthClient"/>. <paramref name="configure"/> runs last and wins, for a test that needs the provider to refuse.
    /// </summary>
    public ILifetimeScope Scope(World world, Guid? userId, Action<ContainerBuilder>? configure = null) => _fixture.BeginScope(b =>
    {
        b.RegisterInstance<IOAuthClientRegistry>(new SingleClientRegistry(world.OAuth)).SingleInstance();
        b.Register<IProviderRegistry>(c => new GitTestProviderRegistry(new ProviderRegistry(c.Resolve<IEnumerable<IProviderCapability>>()))).InstancePerLifetimeScope();

        if (userId is Guid id)
        {
            b.RegisterInstance(new TestCurrentUser(id, "tester")).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(world.TeamId)).As<ICurrentTeam>().SingleInstance();
        }

        configure?.Invoke(b);
    });

    /// <summary>The Providers modal's "Connect with OAuth": Init as the person, then the provider's anonymous callback.</summary>
    public async Task<Guid> ConnectOAuthAsync(World world, Guid userId, string accessToken)
    {
        world.OAuth.ExchangeResult = Token(accessToken, $"rt-{accessToken}");
        world.OAuth.RefreshFailure = null;

        InitCredentialOAuthResult init;

        using (var scope = Scope(world, userId))
            init = await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceId, DisplayName = "owner's GitLab" });

        using var callback = Scope(world, userId: null);

        return (await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code-from-provider", BrowserNonce = init.BrowserNonce })).CredentialId.ShouldNotBeNull();
    }

    /// <summary>The Providers modal's "Use a token": the identity link, which mints a Personal token credential owned by the person.</summary>
    public async Task<Guid> LinkTokenAsync(World world, Guid userId, string token)
    {
        using (var scope = Scope(world, userId))
            await scope.Resolve<IMediator>().Send(new LinkProviderIdentityByPatCommand { ProviderInstanceId = world.InstanceId, AccessToken = token });

        using var verify = _fixture.BeginScope();

        return await verify.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking()
            .Where(c => c.ProviderInstanceId == world.InstanceId && c.OwnerUserId == userId && c.AuthType == AuthType.Pat && c.Status == CredentialStatus.Active)
            .OrderByDescending(c => c.CreatedDate).Select(c => c.Id).FirstAsync();
    }

    /// <summary><c>POST /api/credentials/pat</c>: another live token for its owner, with no identity link.</summary>
    public async Task<Guid> AddTokenAsync(World world, Guid userId, string token, Guid? instanceId = null, CredentialOwnership ownership = CredentialOwnership.Personal)
    {
        using var scope = Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new AddCredentialCommand { ProviderInstanceId = instanceId ?? world.InstanceId, DisplayName = $"{ownership} {token}", Payload = new PatPayload { Token = token }, Ownership = ownership });
    }

    /// <summary>The Personal tab's "Disconnect" — with nothing said about where its repositories go, as the Providers modal sends it today.</summary>
    public async Task<RevokeCredentialResult> DisconnectAsync(World world, Guid userId, Guid credentialId, Guid? moveBindingsTo = null)
    {
        using var scope = Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credentialId, MoveBindingsToCredentialId = moveBindingsTo });
    }

    /// <summary>The Disconnect dialog's preview, asked as <paramref name="userId"/> before anything is disconnected.</summary>
    public async Task<CredentialUsage> UsageAsync(World world, Guid userId, Guid credentialId)
    {
        using var scope = Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new GetCredentialUsageQuery { CredentialId = credentialId });
    }

    /// <summary>The Connected Identities page's "Unlink": the person's one live identity on the world's instance, ended through the mediator.</summary>
    public async Task UnlinkIdentityAsync(World world, Guid userId)
    {
        Guid identityId;

        using (var verify = _fixture.BeginScope())
            identityId = await verify.Resolve<CodeSpaceDbContext>().UserProviderIdentity.AsNoTracking().Where(i => i.UserId == userId && i.ProviderInstanceId == world.InstanceId && i.DeletedDate == null).Select(i => i.Id).SingleAsync();

        using var scope = Scope(world, userId);

        await scope.Resolve<IMediator>().Send(new UnlinkProviderIdentityCommand { IdentityId = identityId });
    }

    /// <summary>
    /// A credential written straight to the table, sealed the way a connect seals it — for a shape no single door produces
    /// (two live tokens for one person, a token held by someone who never joined the team). A revoked one carries the blank
    /// payload a Disconnect leaves; <paramref name="createdDate"/> places it before or after another credential's revocation.
    /// </summary>
    public async Task<Guid> SeedCredentialAsync(World world, Guid? ownerId, string token, CredentialStatus status = CredentialStatus.Active, CredentialOwnership ownership = CredentialOwnership.Personal, Guid? instanceId = null, DateTimeOffset? createdDate = null, Guid? id = null, IReadOnlyList<string>? scopes = null, DateTimeOffset? expiresDate = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var sealedToken = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = token }));
        var created = createdDate ?? DateTimeOffset.UtcNow;

        var credential = new Credential { Id = id ?? Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = instanceId ?? world.InstanceId, OwnerUserId = ownerId, Ownership = ownership, AuthType = AuthType.Pat, DisplayName = $"{ownership} {token}", EncryptedPayload = status == CredentialStatus.Revoked ? string.Empty : sealedToken, Status = status, Scopes = scopes?.ToList(), ExpiresDate = expiresDate, CreatedDate = created, LastModifiedDate = created };

        db.Credential.Add(credential);
        await db.SaveChangesAsync();

        return credential.Id;
    }

    /// <summary>What every repository-bound run does first: resolve the clone URL and token for its repository.</summary>
    public async Task<string?> ResolveCloneTokenAsync(World world, Guid repositoryId)
    {
        using var scope = Scope(world, userId: null);

        var request = await scope.Resolve<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(repositoryId, world.TeamId, CancellationToken.None);

        return request!.Token;
    }

    /// <summary>
    /// A repository bound through <paramref name="credentialId"/>. Its external id is its path, which is what the Git test provider answers a lookup with, so a
    /// probe reads it where CodeSpace has it; <paramref name="storedPath"/> is the path CodeSpace stores it under when that should differ from the provider's — a rename the provider has seen and CodeSpace has not.
    /// </summary>
    public async Task<Guid> SeedRepositoryAsync(World world, Guid credentialId, RepositoryStatus status = RepositoryStatus.Active, Guid? instanceId = null, string? name = null, string? storedPath = null)
    {
        name ??= $"repo-{Guid.NewGuid():N}"[..13];
        var repo = new Repository { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = instanceId ?? world.InstanceId, CredentialId = credentialId, ExternalId = $"acme/{name}", NamespacePath = "acme", Name = name, FullPath = storedPath ?? $"acme/{name}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://git.test/acme/{name}", CloneUrlHttps = $"https://git.test/acme/{name}.git", Status = status, LastError = status == RepositoryStatus.Error ? "Credential disconnected." : null };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Repository.Add(repo);
        await db.SaveChangesAsync();

        return repo.Id;
    }

    /// <summary>
    /// A per-repository hook as a bind leaves it: <paramref name="status"/> and, when Registered, the id the provider gave it. The callback
    /// carries the row's own id, as a bind's does.
    /// </summary>
    public async Task<Guid> SeedRepositoryWebhookAsync(Guid repositoryId, RepositoryWebhookRegistrationStatus status)
    {
        var id = Guid.NewGuid();
        var registered = status == RepositoryWebhookRegistrationStatus.Registered;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.RepositoryWebhook.Add(new RepositoryWebhook { Id = id, RepositoryId = repositoryId, CallbackUrl = $"https://codespace.test/api/webhooks/{id}", SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("hook-secret"), SubscribedEvents = new List<string> { "push" }, RegistrationStatus = status, ExternalId = registered ? $"hook-{id:N}"[..12] : null, RegisteredAt = registered ? DateTimeOffset.UtcNow.AddDays(-20) : null });
        await db.SaveChangesAsync();

        return id;
    }

    public async Task<List<RepositoryWebhook>> LoadRepositoryWebhooksAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().RepositoryWebhook.AsNoTracking().Where(w => w.RepositoryId == repositoryId).OrderBy(w => w.CreatedDate).ToListAsync();
    }

    /// <summary>A group hook on the world's instance, registered through <paramref name="credentialId"/> — failed ones carrying the 401 a dying token returns.</summary>
    public async Task<Guid> SeedConnectionWebhookAsync(World world, Guid credentialId, RepositoryWebhookRegistrationStatus status, Guid? instanceId = null, string ownerPath = "acme")
    {
        var id = Guid.NewGuid();
        var failed = status is RepositoryWebhookRegistrationStatus.Failed or RepositoryWebhookRegistrationStatus.DeadLettered;
        var registered = status == RepositoryWebhookRegistrationStatus.Registered;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ConnectionWebhook.Add(new ConnectionWebhook { Id = id, ProviderInstanceId = instanceId ?? world.InstanceId, CredentialId = credentialId, OwnerPath = ownerPath, CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}", SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("hook-secret"), SubscribedEvents = new List<string> { "push" }, RegistrationStatus = status, Attempts = SeededHookAttempts, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1), LastError = failed ? "401 Unauthorized" : null, ExternalId = registered ? "4242" : null, RegisteredAt = registered ? DateTimeOffset.UtcNow.AddDays(-20) : null });
        await db.SaveChangesAsync();

        return id;
    }

    public const int SeededHookAttempts = 3;

    public async Task<Repository> LoadRepositoryAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
    }

    public async Task<Credential> LoadCredentialAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId);
    }

    public async Task<ConnectionWebhook> LoadConnectionWebhookAsync(Guid hookId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().SingleAsync(w => w.Id == hookId);
    }

    public InMemoryBackgroundJobClient Jobs()
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    public bool WasDispatched(Guid hookId) => Jobs().Calls.Any(c => c.ServiceType == typeof(IConnectionWebhookRegistrar) && Equals(c.FirstArgument, hookId));

    /// <summary>A per-repository hook was handed to its registrar.</summary>
    public bool WasRegistrationDispatched(Guid webhookId) => Jobs().Calls.Any(c => c.ServiceType == typeof(IRepositoryWebhookRegistrar) && Equals(c.FirstArgument, webhookId));

    public static OAuthTokenResponse Token(string accessToken, string? refreshToken) => new() { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = refreshToken == null ? null : DateTimeOffset.UtcNow + TimeSpan.FromHours(2) };

    private static ProviderInstance NewInstance(Guid teamId, string suffix, IPayloadEncryptor encryptor) =>
        new() { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = $"GitLab {suffix}", BaseUrl = $"https://gitlab-{suffix}.test", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("client-secret") };

    private static TeamMembership Membership(Team team, User user, TeamRole role) => new() { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = role };

    /// <summary>Answers the GitLab instance's capability lookups with the Git test provider's, so connecting, token-linking and reading a repository contact nothing real.</summary>
    internal sealed class GitTestProviderRegistry : IProviderRegistry
    {
        private readonly IProviderRegistry _inner;

        public GitTestProviderRegistry(IProviderRegistry inner) { _inner = inner; }

        public TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability => _inner.Require<TCapability>(ProviderKind.Git);

        public bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability => _inner.TryGet(ProviderKind.Git, out capability);

        public IReadOnlyList<Type> GetCapabilities(ProviderKind kind) => _inner.GetCapabilities(ProviderKind.Git);
    }

    private sealed class SingleClientRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;

        public SingleClientRegistry(IOAuthClient client) { _client = client; }

        public IOAuthClient Get(ProviderKind kind) => _client;
    }
}
