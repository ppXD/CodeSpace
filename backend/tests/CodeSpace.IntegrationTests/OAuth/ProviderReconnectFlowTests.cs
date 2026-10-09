using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Identity;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// The path a person takes when a provider connection goes stale: runs start failing, they open Providers,
/// Disconnect the dead credential (the "Connect with OAuth" button only comes back once the old link is gone)
/// and Connect again — or link a token over it. Disconnecting revokes the row and blanks its encrypted
/// payload; reconnecting mints a NEW row. Everything bound through the old one — repositories, and the group hooks
/// it registered — must follow the owner's reconnect: before they did, every run decrypted the blank payload and
/// died on Data Protection's "The provided payload cannot be decrypted because it was not protected with this
/// protection provider", and no workflow edit could reach it because the stale binding lives on the repository row.
/// A binding a connect path left behind anyway (the pre-fix code, or an older pod during a rolling deploy) is moved
/// by the recurring repair sweep under the same rule.
///
/// <para>Real Postgres; the production Data Protection wiring (<see cref="CodeSpaceDataProtection"/> — DB-backed
/// key ring, pinned application name) behind every encrypt and decrypt; the real mediator pipeline for connect,
/// disconnect and token link; and the run's own read, <see cref="IAgentWorkspaceResolver"/>, through the real
/// GitLab auth strategies. Only the provider's edges are stubbed: its token endpoint (<see cref="StubOAuthClient"/>)
/// and its whoami (answered by the Git test provider), so nothing real is contacted.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProviderReconnectFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly AutofacServiceProvider _keyRing;
    private readonly IPayloadEncryptor _encryptor;

    public ProviderReconnectFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _keyRing = BuildProductionKeyRing(fixture.ConnectionString);
        _encryptor = _keyRing.GetRequiredService<IPayloadEncryptor>();
    }

    public void Dispose() => _keyRing.Dispose();

    public enum ReconnectBy { OAuth, TokenLink, CredentialsApi }

    [Theory]
    [InlineData(ReconnectBy.OAuth, "at-reconnected")]
    [InlineData(ReconnectBy.TokenLink, "glpat-reconnected")]
    [InlineData(ReconnectBy.CredentialsApi, "glpat-reconnected")]
    public async Task A_run_after_reconnecting_an_expired_connection_clones_with_the_reconnected_token(ReconnectBy reconnectWith, string reconnectedToken)
    {
        var world = await SeedWorldAsync();
        var stale = await ConnectOAuthAsync(world, world.OwnerId, Token("at-stale", "rt-stale", TimeSpan.FromMinutes(-5)));
        var repoId = await SeedRepositoryAsync(world, stale);

        world.OAuth.RefreshFailure = new OAuthExchangeException("invalid_grant", "The provided authorization grant is invalid, expired, revoked", null);
        await Should.ThrowAsync<OAuthExchangeException>(() => ResolveCloneTokenAsync(world, repoId));

        await DisconnectAsync(world, stale);
        var reconnected = await ReconnectAsync(world, reconnectWith, reconnectedToken);

        var token = await ResolveCloneTokenAsync(world, repoId);

        token.ShouldBe(reconnectedToken, "a run after the reconnect must clone with the reconnected token — not decrypt the disconnected credential's blanked payload");

        var repo = await LoadRepositoryAsync(repoId);
        repo.CredentialId.ShouldBe(reconnected, "the binding follows the owner's reconnect, so the repository row tells the truth about what it authenticates with");
        repo.Status.ShouldBe(RepositoryStatus.Active, "the disconnect's 'needs a new credential' Error is lifted once it has one");
        repo.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Linking_a_token_over_a_live_oauth_link_carries_its_repositories_to_the_token()
    {
        // No Disconnect click: the token link itself retires the OAuth credential behind the identity, blanking it
        // in the same save that writes the token — and the repository bound through it never even turned Error.
        var world = await SeedWorldAsync();
        var oauth = await ConnectOAuthAsync(world, world.OwnerId, Token("at-live", "rt-live", TimeSpan.FromHours(2)));
        var repoId = await SeedRepositoryAsync(world, oauth);

        var pat = await LinkTokenAsync(world, "glpat-replacement");

        (await ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-replacement");

        var repo = await LoadRepositoryAsync(repoId);
        repo.CredentialId.ShouldBe(pat);
        repo.Status.ShouldBe(RepositoryStatus.Active);
    }

    [Fact]
    public async Task Connecting_oauth_beside_a_live_token_leaves_the_tokens_repositories_on_the_token()
    {
        // The modal still offers "Connect with OAuth" to an owner who holds only a token, and connecting re-points the
        // owner's identity WITHOUT revoking the token — so both credentials stay live. Only a revoked credential has a
        // successor: moving a live one's repositories would silently change which grant they act through.
        var world = await SeedWorldAsync();
        var token = await LinkTokenAsync(world, "glpat-live");
        var repoId = await SeedRepositoryAsync(world, token);

        await ConnectOAuthAsync(world, world.OwnerId, Token("at-beside", "rt-beside", TimeSpan.FromHours(2)));

        (await LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(token, "the token is still live, so the repository bound through it is not the OAuth connect's to take");
        (await ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-live");
    }

    [Theory]
    [InlineData(RepositoryWebhookRegistrationStatus.DeadLettered, RepositoryWebhookRegistrationStatus.Enqueued, true)]
    [InlineData(RepositoryWebhookRegistrationStatus.Failed, RepositoryWebhookRegistrationStatus.Enqueued, true)]
    [InlineData(RepositoryWebhookRegistrationStatus.Registered, RepositoryWebhookRegistrationStatus.Registered, true)]
    [InlineData(RepositoryWebhookRegistrationStatus.Cancelled, RepositoryWebhookRegistrationStatus.Cancelled, false)]
    public async Task A_group_hook_the_disconnected_credential_registered_follows_the_reconnect(RepositoryWebhookRegistrationStatus before, RepositoryWebhookRegistrationStatus after, bool follows)
    {
        // A group hook records the credential that registered it, and registers, retries and deletes through THAT
        // credential. One that failed while the token was dying (the invalid_grant that started the reconnect) could
        // otherwise never register again: nothing re-points it, and connection hooks have no retry of their own. A hook
        // in service follows the owner; a failed or dead-lettered one is revived and dispatched, as a bind revives it; a
        // Registered one keeps delivering and only needs a live credential for its eventual delete; a Cancelled one is
        // retired and stays where it is.
        var world = await SeedWorldAsync();
        var stale = await ConnectOAuthAsync(world, world.OwnerId, Token("at-stale", "rt-stale", TimeSpan.FromHours(2)));
        var hookId = await SeedConnectionWebhookAsync(world, stale, before);

        await DisconnectAsync(world, stale);

        using var dispatches = Jobs().ManualExecution();
        var reconnected = await ConnectOAuthAsync(world, world.OwnerId, Token("at-reconnected", "rt-reconnected", TimeSpan.FromHours(2)));

        var hook = await LoadConnectionWebhookAsync(hookId);

        hook.CredentialId.ShouldBe(follows ? reconnected : stale, $"a {before} hook {(follows ? "is in service, so it follows the owner's reconnect" : "is retired, so it stays")}");
        hook.RegistrationStatus.ShouldBe(after, $"a {before} hook {(after == RepositoryWebhookRegistrationStatus.Enqueued ? "is revived and dispatched once the connect commits" : "keeps its status")}");
        hook.Attempts.ShouldBe(after == RepositoryWebhookRegistrationStatus.Enqueued ? 0 : SeededHookAttempts, "a revival resets the backoff ladder, exactly as a bind's does");
        WasDispatched(hookId).ShouldBe(after == RepositoryWebhookRegistrationStatus.Enqueued, "only a revived hook is handed to the registrar");

        if (!follows) return;

        (await ResolveHookTokenAsync(world, hookId)).ShouldBe("at-reconnected", "the registrar authenticates through the hook's own credential — a disconnected one could never register it again");
    }

    [Fact]
    public async Task Another_members_connect_does_not_adopt_a_disconnected_credentials_repositories()
    {
        // Succession follows the OWNER of the disconnected credential. A teammate connecting the same provider is a
        // different identity — moving the owner's repositories onto it would silently change who they act as.
        var world = await SeedWorldAsync();
        var ownersCredential = await ConnectOAuthAsync(world, world.OwnerId, Token("at-owner", "rt-owner", TimeSpan.FromHours(2)));
        var repoId = await SeedRepositoryAsync(world, ownersCredential);

        await DisconnectAsync(world, ownersCredential);
        await ConnectOAuthAsync(world, world.MemberId, Token("at-member", "rt-member", TimeSpan.FromHours(2)));

        var repo = await LoadRepositoryAsync(repoId);
        repo.CredentialId.ShouldBe(ownersCredential);
        repo.Status.ShouldBe(RepositoryStatus.Error, "the owner's repositories still need the owner (or a deliberate re-link)");
    }

    [Fact]
    public async Task A_run_on_a_disconnect_nobody_reconnected_names_the_disconnect()
    {
        var world = await SeedWorldAsync();
        var credential = await ConnectOAuthAsync(world, world.OwnerId, Token("at-live", "rt-live", TimeSpan.FromHours(2)));
        var repoId = await SeedRepositoryAsync(world, credential);

        await DisconnectAsync(world, credential);

        var failure = await Should.ThrowAsync<CredentialDisconnectedException>(() => ResolveCloneTokenAsync(world, repoId));

        failure.CredentialId.ShouldBe(credential);
        failure.Message.ShouldContain("'owner's GitLab' was disconnected", Case.Sensitive, "the run must say which credential is gone and what to do — not report the blanked payload as a Data Protection fault");
    }

    public enum StrandedShape { ReconnectedAfterTheDisconnect, TokenLinkedInTheSameSave, OnlyAnOlderLiveCredential, ReconnectedByAnotherMember, ReconnectedThenDisconnectedAgain, BoundToALiveCredentialWithANewerSibling }

    [Theory]
    [InlineData(StrandedShape.ReconnectedAfterTheDisconnect, true)]
    [InlineData(StrandedShape.TokenLinkedInTheSameSave, true)]
    [InlineData(StrandedShape.OnlyAnOlderLiveCredential, false)]
    [InlineData(StrandedShape.ReconnectedByAnotherMember, false)]
    [InlineData(StrandedShape.ReconnectedThenDisconnectedAgain, false)]
    [InlineData(StrandedShape.BoundToALiveCredentialWithANewerSibling, false)]
    public async Task The_repair_sweep_moves_a_stranded_binding_only_to_its_owners_reconnect(StrandedShape shape, bool moves)
    {
        // The rows a connect path without the carry-forward leaves behind — the code before it, or an older pod still
        // serving callbacks during a rolling deploy — written as it left them: a credential with a repository and a
        // group hook still bound to it, and whatever was connected around it. The recurring sweep must apply the rule
        // the connect paths apply, on every tick, and leave alone every shape that rule leaves alone.
        var world = await SeedWorldAsync();
        var (bound, successor) = await SeedStrandedCredentialsAsync(world, shape);
        var repoId = await SeedRepositoryAsync(world, bound, shape is StrandedShape.TokenLinkedInTheSameSave or StrandedShape.BoundToALiveCredentialWithANewerSibling ? RepositoryStatus.Active : RepositoryStatus.Error);
        var hookId = await SeedConnectionWebhookAsync(world, bound, RepositoryWebhookRegistrationStatus.DeadLettered);

        using var dispatches = Jobs().ManualExecution();
        await SweepUntilDrainedAsync();
        await SweepUntilDrainedAsync();

        var repo = await LoadRepositoryAsync(repoId);
        var hook = await LoadConnectionWebhookAsync(hookId);

        if (!moves)
        {
            repo.CredentialId.ShouldBe(bound, $"{shape}: no reconnect of the owner's to follow, so the repository stays where a person put it");
            hook.CredentialId.ShouldBe(bound, $"{shape}: the group hook follows the same rule as the repository");
            hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.DeadLettered, $"{shape}: a hook that did not move is not revived either");
            await ShouldStillCloneThroughTheBoundCredentialAsync(world, repoId, shape, bound);
            return;
        }

        repo.CredentialId.ShouldBe(successor, $"{shape}: the stranded repository follows its owner's reconnect, and a second drain moves nothing");
        repo.Status.ShouldBe(RepositoryStatus.Active);
        repo.LastError.ShouldBeNull();
        (await ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-successor", "an existing production row must clone again after the deploy, with nobody hand-editing it");

        hook.CredentialId.ShouldBe(successor, $"{shape}: the group hook follows its owner's reconnect with the repository");
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued, "a dead-lettered hook that gained a live credential is revived and dispatched");
        WasDispatched(hookId).ShouldBeTrue("the revived hook is handed to the registrar, not left Pending for a bind that may never come");
    }

    [Fact]
    public async Task The_repair_sweep_moves_a_group_hook_that_is_the_disconnected_credentials_only_binding()
    {
        // Someone who re-linked every repository by hand after the disconnect leaves only the group hook on the dead
        // credential: still a binding, and still unable to register.
        var world = await SeedWorldAsync();
        var (dead, successor) = await SeedStrandedCredentialsAsync(world, StrandedShape.ReconnectedAfterTheDisconnect);
        var hookId = await SeedConnectionWebhookAsync(world, dead, RepositoryWebhookRegistrationStatus.Failed);

        using var dispatches = Jobs().ManualExecution();
        await SweepUntilDrainedAsync();

        var hook = await LoadConnectionWebhookAsync(hookId);

        hook.CredentialId.ShouldBe(successor, "a credential with nothing but a hook bound to it is still stranded");
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued);
        WasDispatched(hookId).ShouldBeTrue();
    }

    // ── Flow steps ────────────────────────────────────────────────────────────────

    private async Task<Guid> ReconnectAsync(World world, ReconnectBy method, string token) => method switch
    {
        ReconnectBy.OAuth => await ConnectOAuthAsync(world, world.OwnerId, Token(token, "rt-reconnected", TimeSpan.FromHours(2))),
        ReconnectBy.TokenLink => await LinkTokenAsync(world, token),
        ReconnectBy.CredentialsApi => await AddTokenCredentialAsync(world, token),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "reconnect is OAuth, a token link, or a token added through the credentials API")
    };

    /// <summary>The Providers modal's "Connect with OAuth": Init as the person, then the provider's anonymous callback.</summary>
    private async Task<Guid> ConnectOAuthAsync(World world, Guid userId, OAuthTokenResponse issued)
    {
        world.OAuth.ExchangeResult = issued;
        world.OAuth.RefreshFailure = null;

        InitCredentialOAuthResult init;

        using (var scope = Scope(world, userId))
            init = await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceId, DisplayName = "owner's GitLab" });

        using var callback = Scope(world, userId: null);

        return (await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code-from-provider" })).CredentialId;
    }

    /// <summary>The Providers modal's "Use a token".</summary>
    private async Task<Guid> LinkTokenAsync(World world, string token)
    {
        using (var scope = Scope(world, world.OwnerId))
            await scope.Resolve<IMediator>().Send(new LinkProviderIdentityByPatCommand { ProviderInstanceId = world.InstanceId, AccessToken = token });

        using var verify = _fixture.BeginScope();

        return await verify.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking()
            .Where(c => c.ProviderInstanceId == world.InstanceId && c.OwnerUserId == world.OwnerId && c.AuthType == AuthType.Pat && c.Status == CredentialStatus.Active)
            .Select(c => c.Id).SingleAsync();
    }

    /// <summary><c>POST /api/credentials/pat</c>: a token credential added for its owner, with no identity link.</summary>
    private async Task<Guid> AddTokenCredentialAsync(World world, string token)
    {
        using var scope = Scope(world, world.OwnerId);

        return await scope.Resolve<IMediator>().Send(new AddCredentialCommand { ProviderInstanceId = world.InstanceId, OwnerUserId = world.OwnerId, DisplayName = "owner's token", Payload = new PatPayload { Token = token } });
    }

    /// <summary>The recurring repair's tick, through the real mediator pipeline the job uses.</summary>
    private async Task SweepUntilDrainedAsync()
    {
        // The sweep is bounded and deployment-wide, and this suite shares one database: run it until a pass repairs
        // nothing, so every candidate — this world's included — has been reached. A negative shape then cannot pass
        // just because a wider rule never got round to it.
        for (var pass = 1; pass <= MaxSweepPasses; pass++)
        {
            using var scope = _fixture.BeginScope();

            if (await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand()) == 0) return;
        }

        throw new ShouldAssertException($"RepairDisconnectedCredentialBindingsCommand still repaired bindings after {MaxSweepPasses} passes — a repaired credential is not leaving the candidate set. Check that the sweep's candidate query drops a revoked credential once nothing live is bound to it.");
    }

    /// <summary>The Personal tab's "Disconnect".</summary>
    private async Task DisconnectAsync(World world, Guid credentialId)
    {
        using var scope = Scope(world, world.OwnerId);

        await scope.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credentialId });
    }

    /// <summary>What every repository-bound run does first: resolve the clone URL and token for its repository.</summary>
    private async Task<string?> ResolveCloneTokenAsync(World world, Guid repositoryId)
    {
        using var scope = Scope(world, userId: null);

        var request = await scope.Resolve<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(repositoryId, world.TeamId, CancellationToken.None);

        return request!.Token;
    }

    /// <summary>What the group hook's registrar does first: authenticate through the hook's own credential.</summary>
    private async Task<string> ResolveHookTokenAsync(World world, Guid hookId)
    {
        using var scope = Scope(world, userId: null);

        var hook = await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().Include(w => w.ProviderInstance).Include(w => w.Credential).SingleAsync(w => w.Id == hookId);
        var auth = await scope.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(hook.ProviderInstance, hook.Credential), CancellationToken.None);

        return auth.Token;
    }

    private async Task ShouldStillCloneThroughTheBoundCredentialAsync(World world, Guid repositoryId, StrandedShape shape, Guid bound)
    {
        if (shape == StrandedShape.BoundToALiveCredentialWithANewerSibling)
        {
            (await ResolveCloneTokenAsync(world, repositoryId)).ShouldBe("glpat-bound", "a repository on a live credential keeps cloning through its own grant, whatever else its owner connected");
            return;
        }

        var failure = await Should.ThrowAsync<CredentialDisconnectedException>(() => ResolveCloneTokenAsync(world, repositoryId));

        failure.CredentialId.ShouldBe(bound, $"{shape}: the run names the disconnect it is still bound to");
    }

    // ── World ─────────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid InstanceId, Guid OwnerId, Guid MemberId, StubOAuthClient OAuth);

    private async Task<World> SeedWorldAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "owner" };
        var member = new User { Id = Guid.NewGuid(), Email = $"member-{suffix}@x", Name = "member" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = $"https://gitlab-{suffix}.test", OauthClientId = "client", OauthClientSecretEnc = _encryptor.Encrypt("client-secret") };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Team.Add(team);
        db.User.AddRange(owner, member);
        db.TeamMembership.AddRange(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner }, new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member.Id, Role = TeamRole.Admin });
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return new World(team.Id, instance.Id, owner.Id, member.Id, new StubOAuthClient(ProviderKind.GitLab, Token("unused", null, null)));
    }

    /// <summary>
    /// The credential rows of one stranded shape, timestamped as a connect path without the carry-forward wrote them:
    /// (the one the repository and hook are bound to, the one connected around it). The bound one is Revoked with a
    /// blank payload, except in <see cref="StrandedShape.BoundToALiveCredentialWithANewerSibling"/>, where it is live.
    /// </summary>
    private async Task<(Guid Bound, Guid Other)> SeedStrandedCredentialsAsync(World world, StrandedShape shape)
    {
        var connected = DateTimeOffset.UtcNow.AddDays(-30);
        var revoked = connected.AddDays(10);

        var otherOwner = shape == StrandedShape.ReconnectedByAnotherMember ? world.MemberId : world.OwnerId;
        var otherConnected = shape switch
        {
            StrandedShape.TokenLinkedInTheSameSave => revoked,
            StrandedShape.OnlyAnOlderLiveCredential => connected.AddDays(-1),
            StrandedShape.BoundToALiveCredentialWithANewerSibling => connected.AddDays(1),
            _ => revoked.AddDays(1)
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var bound = shape == StrandedShape.BoundToALiveCredentialWithANewerSibling
            ? StrandedCredential(world, world.OwnerId, AuthType.Pat, CredentialStatus.Active, SealToken(scope, "glpat-bound"), connected, connected)
            : StrandedCredential(world, world.OwnerId, AuthType.OAuth, CredentialStatus.Revoked, string.Empty, connected, revoked);

        var other = shape == StrandedShape.ReconnectedThenDisconnectedAgain
            ? StrandedCredential(world, otherOwner, AuthType.OAuth, CredentialStatus.Revoked, string.Empty, otherConnected, otherConnected.AddDays(1))
            : StrandedCredential(world, otherOwner, AuthType.Pat, CredentialStatus.Active, SealToken(scope, "glpat-successor"), otherConnected, otherConnected);

        if (shape == StrandedShape.TokenLinkedInTheSameSave) bound.DeletedDate = revoked;

        db.Credential.AddRange(bound, other);
        await db.SaveChangesAsync();

        return (bound.Id, other.Id);
    }

    private string SealToken(ILifetimeScope scope, string token) => _encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = token }));

    private static Credential StrandedCredential(World world, Guid ownerId, AuthType authType, CredentialStatus status, string encryptedPayload, DateTimeOffset createdDate, DateTimeOffset lastModifiedDate) =>
        new() { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = world.InstanceId, OwnerUserId = ownerId, AuthType = authType, DisplayName = "owner's GitLab", Status = status, EncryptedPayload = encryptedPayload, CreatedDate = createdDate, LastModifiedDate = lastModifiedDate };

    private const int SeededHookAttempts = 3;
    private const int MaxSweepPasses = 20;

    /// <summary>A group hook on the world's instance, registered through <paramref name="credentialId"/>, in <paramref name="status"/> — failed ones carrying the 401 a dying token returns.</summary>
    private async Task<Guid> SeedConnectionWebhookAsync(World world, Guid credentialId, RepositoryWebhookRegistrationStatus status)
    {
        var id = Guid.NewGuid();
        var failed = status is RepositoryWebhookRegistrationStatus.Failed or RepositoryWebhookRegistrationStatus.DeadLettered;
        var registered = status == RepositoryWebhookRegistrationStatus.Registered;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ConnectionWebhook.Add(new ConnectionWebhook { Id = id, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, OwnerPath = "acme", CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}", SecretEnc = _encryptor.Encrypt("hook-secret"), SubscribedEvents = new List<string> { "push" }, RegistrationStatus = status, Attempts = SeededHookAttempts, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1), LastError = failed ? "401 Unauthorized" : null, ExternalId = registered ? "4242" : null, RegisteredAt = registered ? DateTimeOffset.UtcNow.AddDays(-20) : null });
        await db.SaveChangesAsync();

        return id;
    }

    private async Task<ConnectionWebhook> LoadConnectionWebhookAsync(Guid hookId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().SingleAsync(w => w.Id == hookId);
    }

    private InMemoryBackgroundJobClient Jobs()
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private bool WasDispatched(Guid hookId) => Jobs().Calls.Any(c => c.ServiceType == typeof(IConnectionWebhookRegistrar) && Equals(c.FirstArgument, hookId));

    private async Task<Guid> SeedRepositoryAsync(World world, Guid credentialId, RepositoryStatus status = RepositoryStatus.Active)
    {
        var name = $"repo-{Guid.NewGuid():N}"[..13];
        var repo = new Repository { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, ExternalId = Guid.NewGuid().ToString("N"), NamespacePath = "acme", Name = name, FullPath = $"acme/{name}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://gitlab.test/acme/{name}", CloneUrlHttps = $"https://gitlab.test/acme/{name}.git", Status = status, LastError = status == RepositoryStatus.Error ? "Credential disconnected. Re-link this repository to an active credential of the same provider, or unbind it." : null };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Repository.Add(repo);
        await db.SaveChangesAsync();

        return repo.Id;
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
    }

    private ILifetimeScope Scope(World world, Guid? userId) => _fixture.BeginScope(b =>
    {
        b.RegisterInstance(_encryptor).As<IPayloadEncryptor>().SingleInstance();
        b.RegisterInstance<IOAuthClientRegistry>(new SingleClientRegistry(world.OAuth)).SingleInstance();
        b.Register<IProviderRegistry>(c => new GitTestProviderRegistry(new ProviderRegistry(c.Resolve<IEnumerable<IProviderCapability>>()))).InstancePerLifetimeScope();

        if (userId is not Guid id) return;

        b.RegisterInstance(new TestCurrentUser(id, "tester")).As<ICurrentUser>().SingleInstance();
        b.RegisterInstance(new TestCurrentTeam(world.TeamId)).As<ICurrentTeam>().SingleInstance();
    });

    private static OAuthTokenResponse Token(string accessToken, string? refreshToken, TimeSpan? expiresIn) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresAt = expiresIn.HasValue ? DateTimeOffset.UtcNow + expiresIn.Value : null
    };

    /// <summary>
    /// The production Data Protection wiring, persisted to this test database: every credential in these flows is
    /// sealed and opened through the same key ring a deployment uses, so a failure here cannot be a test-only key
    /// ring's artefact.
    /// </summary>
    private static AutofacServiceProvider BuildProductionKeyRing(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddCodeSpaceDataProtection();
        services.AddSingleton<IPayloadEncryptor, DataProtectionPayloadEncryptor>();

        var builder = new ContainerBuilder();
        builder.Populate(services);
        builder.Register(_ => new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
        builder.Register(c => new CodeSpaceDbContext(c.Resolve<DbContextOptions<CodeSpaceDbContext>>())).AsSelf().InstancePerLifetimeScope();

        return new AutofacServiceProvider(builder.Build());
    }

    private sealed class SingleClientRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;
        public SingleClientRegistry(IOAuthClient client) { _client = client; }
        public IOAuthClient Get(ProviderKind kind) => _client;
    }

    /// <summary>Answers the GitLab instance's capability lookups with the Git test provider's (a canned valid whoami), so connecting and token-linking probe nothing real.</summary>
    private sealed class GitTestProviderRegistry : IProviderRegistry
    {
        private readonly IProviderRegistry _inner;
        public GitTestProviderRegistry(IProviderRegistry inner) { _inner = inner; }
        public TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability => _inner.Require<TCapability>(ProviderKind.Git);
        public bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability => _inner.TryGet(ProviderKind.Git, out capability);
        public IReadOnlyList<Type> GetCapabilities(ProviderKind kind) => _inner.GetCapabilities(ProviderKind.Git);
    }
}
