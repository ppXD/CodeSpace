using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.ProviderInstances;

/// <summary>
/// Update + Delete coverage for provider_instance. Together these unstick the case where
/// an early-created provider has no OAuth credentials (so it's filtered out of the
/// "connectable" list) and the user has no way back — they now have both an Edit and a
/// Remove escape hatch.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class UpdateAndDeleteProviderInstanceFlowTests
{
    private readonly PostgresFixture _fixture;

    public UpdateAndDeleteProviderInstanceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ── Update ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_fills_in_missing_OAuth_credentials_on_existing_provider()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: false).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new UpdateProviderInstanceCommand
        {
            ProviderInstanceId = instanceId,
            OauthClientId = "new-client-id",
            OauthClientSecret = "new-secret",
            OauthDefaultScopes = new[] { "repo", "read:user" }
        }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var encryptor = verify.Resolve<IPayloadEncryptor>();
        var saved = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);

        saved.OauthClientId.ShouldBe("new-client-id");
        encryptor.Decrypt(saved.OauthClientSecretEnc!).ShouldBe("new-secret");
        saved.OauthDefaultScopes.ShouldBe(new[] { "repo", "read:user" });
    }

    [Fact]
    public async Task Update_with_empty_OAuth_secret_preserves_existing_secret()
    {
        // The form's "leave blank to keep" semantics — an empty password input must NEVER
        // accidentally wipe a stored secret. Asserts the handler's
        // !string.IsNullOrEmpty(OauthClientSecret) guard.
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new UpdateProviderInstanceCommand
        {
            ProviderInstanceId = instanceId,
            DisplayName = "Renamed",
            OauthClientSecret = ""   // empty string = "no rotate"
        }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var encryptor = verify.Resolve<IPayloadEncryptor>();
        var saved = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);

        saved.DisplayName.ShouldBe("Renamed");
        encryptor.Decrypt(saved.OauthClientSecretEnc!).ShouldBe("original-secret");
    }

    [Fact]
    public async Task Update_rejects_when_post_edit_tuple_collides_with_another_active_instance()
    {
        // Both rows in the same team:
        //   firstId  → github.com   + client-id="client-id"  (OAuth-ready, seeded with that literal client)
        //   secondId → github.acme  + null client
        // Trying to point secondId at github.com WITHOUT setting its own client_id collides
        // (both rows would have client_id=null on github.com, which is the duplicate case).
        var (teamId, firstId) = await SeedProviderAsync(withOAuth: true, baseUrl: "https://github.com").ConfigureAwait(false);
        var (_, secondId) = await SeedProviderAsync(withOAuth: false, baseUrl: "https://github.acme.local", existingTeamId: teamId).ConfigureAwait(false);

        // Make firstId's row null-client so the post-edit secondId collides with it.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var first = await db.ProviderInstance.SingleAsync(p => p.Id == firstId).ConfigureAwait(false);
            first.OauthClientId = null;
            first.OauthClientSecretEnc = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var caller = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = caller.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new UpdateProviderInstanceCommand
            {
                ProviderInstanceId = secondId,
                BaseUrl = "https://github.com"   // collides with firstId (both null client_id)
            })).ConfigureAwait(false);

        ex.Message.ShouldContain("github.com");
    }

    [Fact]
    public async Task Update_allows_moving_to_same_host_when_client_id_differs()
    {
        // Mirror of the Add multi-app test, but via Edit: take a non-OAuth provider on a
        // dummy host, fill it in with a NEW client_id that doesn't collide with the
        // existing github.com row → must succeed (the multi-app pattern works through
        // Edit too, not only through fresh Add).
        var (teamId, firstId) = await SeedProviderAsync(withOAuth: true, baseUrl: "https://github.com").ConfigureAwait(false);
        var (_, secondId) = await SeedProviderAsync(withOAuth: false, baseUrl: "https://github.acme.local", existingTeamId: teamId).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new UpdateProviderInstanceCommand
        {
            ProviderInstanceId = secondId,
            BaseUrl = "https://github.com",
            OauthClientId = "different-client",
            OauthClientSecret = "different-secret"
        }).ConfigureAwait(false);

        // Both rows now exist on github.com with different client_ids — exactly the
        // intended multi-app shape. Sanity-check by re-loading.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rows = await db.ProviderInstance.AsNoTracking()
            .Where(p => (p.Id == firstId || p.Id == secondId) && p.DeletedDate == null)
            .OrderBy(p => p.OauthClientId)
            .ToListAsync().ConfigureAwait(false);

        rows.Count.ShouldBe(2);
        rows.All(r => r.BaseUrl == "https://github.com").ShouldBeTrue();
        rows.Select(r => r.OauthClientId).ShouldBe(new[] { "client-id", "different-client" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Update_rejects_unknown_instance_with_not_found()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => mediator.Send(new UpdateProviderInstanceCommand
            {
                ProviderInstanceId = Guid.NewGuid(),
                DisplayName = "Whatever"
            })).ConfigureAwait(false);
    }

    // ── Delete ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_soft_deletes_provider_when_no_dependencies()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: false).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var saved = await db.ProviderInstance.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);

        saved.DeletedDate.ShouldNotBeNull();
    }

    /// <summary>
    /// Deleting a connection ends every token connected through it the way any other retirement does — blanked, Revoked,
    /// soft-deleted, the act-as identities linked to it gone — and drops the connection's OAuth client secret, so none of
    /// it stays decryptable in the database. Each OAuth grant is then revoked at the provider, but only once the delete
    /// has committed: a provider asked from inside the transaction would have lost the grant even if the delete rolled
    /// back. The recorder reads the credential through a connection of its own at the moment the provider is asked, which
    /// is what anyone outside the delete's transaction would see.
    /// </summary>
    [Fact]
    public async Task Delete_retires_its_credentials_and_client_secret_then_revokes_each_grant_at_the_provider_after_commit()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId).ConfigureAwait(false);
        var identityId = await SeedIdentityAsync(teamId, instanceId, credentialId).ConfigureAwait(false);
        var provider = new RevocationRecorder(() => ReadCommittedPayloadAsync(credentialId));

        var result = await DeleteAsync(teamId, instanceId, provider).ConfigureAwait(false);

        result.RevokedCredentialCount.ShouldBe(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var cred = await db.Credential.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        var instance = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);
        var identity = await db.UserProviderIdentity.AsNoTracking().SingleAsync(i => i.Id == identityId).ConfigureAwait(false);

        cred.Status.ShouldBe(CredentialStatus.Revoked);
        cred.DeletedDate.ShouldNotBeNull();
        cred.EncryptedPayload.ShouldBe(string.Empty, "a deleted connection's tokens must not stay decryptable in the database");
        cred.LastError.ShouldBe("Provider instance deleted");
        instance.OauthClientSecretEnc.ShouldBeNull("the deleted connection's OAuth client secret must not stay decryptable either");
        instance.OauthClientId.ShouldBe("client-id", "the client id is no secret and stays for the audit trail");
        identity.DeletedDate.ShouldNotBeNull("an act-as identity is unusable without its credential and goes with it, as on every other retirement");

        provider.Revokes.Select(r => (r.Input.Token, r.Input.TokenTypeHint)).ShouldBe(new[] { ("rt-CANARY", "refresh_token"), ("at-CANARY", "access_token") });
        provider.Revokes.ShouldAllBe(r => r.Input.ClientId == "client-id" && r.Input.ClientSecret == "original-secret");
        provider.Revokes.ShouldAllBe(r => !r.Input.WholeGrant, "a delete ends the tokens this connection held, never the user's whole grant: another team connected through the same OAuth App holds tokens under it too");
        provider.Revokes.ShouldAllBe(r => r.CommittedPayload == string.Empty, "the provider was asked while the delete was uncommitted — a rolled-back delete would leave Active credentials on a grant the provider already ended");
    }

    [Fact]
    public async Task Delete_retires_its_credentials_even_when_the_provider_refuses_the_revoke()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId).ConfigureAwait(false);
        var provider = new RevocationRecorder(() => ReadCommittedPayloadAsync(credentialId)) { Refuse = true };

        var result = await DeleteAsync(teamId, instanceId, provider).ConfigureAwait(false);

        result.RevokedCredentialCount.ShouldBe(1, "the provider's answer is best effort; the delete stands without it");
        provider.Revokes.ShouldNotBeEmpty("fixture check: the provider was asked");
        (await ReadCommittedPayloadAsync(credentialId).ConfigureAwait(false)).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Delete_blanks_a_personal_access_token_without_asking_the_provider()
    {
        // A PAT has no revocation endpoint — only its owner deleting it on the provider's side ends it there.
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId, new PatPayload { Token = "glpat-CANARY" }).ConfigureAwait(false);
        var provider = new RevocationRecorder(() => ReadCommittedPayloadAsync(credentialId));

        await DeleteAsync(teamId, instanceId, provider).ConfigureAwait(false);

        provider.Revokes.ShouldBeEmpty();
        (await ReadCommittedPayloadAsync(credentialId).ConfigureAwait(false)).ShouldBe(string.Empty);
    }

    /// <summary>
    /// A connection-wide connection registered its group hooks at the provider with one of its own credentials, and the
    /// delete blanks every one of those. So the hooks come down first, while that token still opens: afterwards nothing
    /// could remove them, and each would go on delivering the group's events with its HMAC secret still decryptable here.
    /// A provider that refuses the delete changes nothing here — the row goes either way, as on a scope switch — and the
    /// hook rows the connection keeps are out of service and hold no secret.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_takes_its_group_hooks_down_with_a_live_token_and_keeps_no_hook_secret(bool providerRefuses)
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true, webhookScope: ProviderWebhookScope.Connection).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId, new PatPayload { Token = "ghp-HOOK-CANARY" }).ConfigureAwait(false);
        await SeedConnectionHookAsync(instanceId, credentialId, "acme", RepositoryWebhookRegistrationStatus.Registered, "hook-777").ConfigureAwait(false);
        var retiredEarlier = await SeedConnectionHookAsync(instanceId, credentialId, "acme/old", RepositoryWebhookRegistrationStatus.Cancelled, externalId: null).ConfigureAwait(false);
        var groupHooks = new GroupHookRecorder { Refuse = providerRefuses };

        await DeleteAsync(teamId, instanceId, new RevocationRecorder(() => ReadCommittedPayloadAsync(credentialId)), groupHooks: groupHooks).ConfigureAwait(false);

        groupHooks.Deletes.Select(d => (d.OwnerPath, d.ExternalId, ReadPatToken(d.Payload))).ShouldBe(new[] { ("acme", "hook-777", "ghp-HOOK-CANARY") }, ignoreOrder: false, customMessage: "the group hook must be deleted at the provider with the token that registered it, before the delete blanks that token — check the hook retirement in ProviderInstanceService.DeleteAsync");

        var hooks = await LoadConnectionHooksAsync(instanceId).ConfigureAwait(false);

        hooks.Select(h => h.Id).ShouldBe(new[] { retiredEarlier }, "the Registered row described a hook that is gone (or that the operator now removes by hand); keeping it would claim coverage for a deleted connection");
        hooks.ShouldAllBe(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Cancelled && h.SecretEnc == string.Empty, "a deleted connection keeps no hook in service and no hook secret decryptable");
    }

    [Fact]
    public async Task Delete_without_force_refuses_when_active_repositories_still_bound()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId).ConfigureAwait(false);
        await SeedRepositoryAsync(teamId, instanceId, credentialId).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId, Force = false })).ConfigureAwait(false);

        // Error names the repo count and points the operator at the cascade option,
        // not just "go find them yourself".
        ex.Message.ShouldContain("repositor");
        ex.Message.ShouldContain("Unbind");
        ex.Message.ShouldContain("unbind all");
    }

    [Fact]
    public async Task Delete_with_force_cascades_unbind_then_removes_provider()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId).ConfigureAwait(false);
        var repoIdA = await SeedRepositoryAsync(teamId, instanceId, credentialId, name: "api").ConfigureAwait(false);
        var repoIdB = await SeedRepositoryAsync(teamId, instanceId, credentialId, name: "web").ConfigureAwait(false);

        var result = await DeleteAsync(teamId, instanceId, new RevocationRecorder(() => ReadCommittedPayloadAsync(credentialId)), force: true).ConfigureAwait(false);

        result.UnboundRepositoryCount.ShouldBe(2);
        result.RevokedCredentialCount.ShouldBe(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // Provider: soft-deleted.
        var providerRow = await db.ProviderInstance.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);
        providerRow.DeletedDate.ShouldNotBeNull();

        // Both repos: soft-deleted.
        var repoRows = await db.Repository.AsNoTracking().IgnoreQueryFilters().Where(r => r.Id == repoIdA || r.Id == repoIdB).ToListAsync().ConfigureAwait(false);
        repoRows.Count.ShouldBe(2);
        repoRows.ShouldAllBe(r => r.DeletedDate != null);

        // Credential: revoked, and its token gone.
        var credRow = await db.Credential.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        credRow.Status.ShouldBe(CredentialStatus.Revoked);
        credRow.DeletedDate.ShouldNotBeNull();
        credRow.EncryptedPayload.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Delete_with_force_when_no_repositories_still_works()
    {
        // Force=true is a superset — must not require any active repos to exist. Tests
        // the no-cascade path through the same flag so callers don't have to branch.
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: false).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId, Force = true }).ConfigureAwait(false);

        result.UnboundRepositoryCount.ShouldBe(0);
        result.RevokedCredentialCount.ShouldBe(0);
    }

    // ── seed helpers ────────────────────────────────────────────────────────────

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var project = TestProjectSeed.BuildDefaultProject(team.Id, owner.Id);

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.Project.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return team.Id;
    }

    private async Task<(Guid TeamId, Guid InstanceId)> SeedProviderAsync(bool withOAuth, string? baseUrl = null, Guid? existingTeamId = null, ProviderWebhookScope webhookScope = ProviderWebhookScope.Repository)
    {
        var teamId = existingTeamId ?? await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.GitHub,
            DisplayName = "GitHub",
            BaseUrl = baseUrl ?? "https://github.com",
            OauthClientId = withOAuth ? "client-id" : null,
            OauthClientSecretEnc = withOAuth ? encryptor.Encrypt("original-secret") : null,
            WebhookScope = webhookScope
        };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (teamId, instance.Id);
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, Guid instanceId, CredentialPayload? token = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var payload = token ?? new OAuthPayload { AccessToken = "at-CANARY", RefreshToken = "rt-CANARY" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instanceId,
            AuthType = payload.Type,
            DisplayName = payload.Type.ToString(),
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)),
            Status = CredentialStatus.Active
        };

        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    /// <summary>The team owner's act-as link to the credential, as connecting through OAuth leaves one.</summary>
    private async Task<Guid> SeedIdentityAsync(Guid teamId, Guid instanceId, Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var ownerId = await db.TeamMembership.AsNoTracking().Where(m => m.TeamId == teamId).Select(m => m.UserId).SingleAsync().ConfigureAwait(false);
        var identity = new UserProviderIdentity { Id = Guid.NewGuid(), UserId = ownerId, ProviderInstanceId = instanceId, CredentialId = credentialId, ProviderUserId = "42", ProviderUsername = "owner" };

        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return identity.Id;
    }

    /// <summary>
    /// The delete as an Admin sends it, with the provider's revocation endpoint replaced by <paramref name="provider"/> and,
    /// when given, its group-hook endpoint by <paramref name="groupHooks"/>.
    /// </summary>
    private async Task<DeleteProviderInstanceResult> DeleteAsync(Guid teamId, Guid instanceId, RevocationRecorder provider, bool force = false, GroupHookRecorder? groupHooks = null)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        using var delete = scope.BeginLifetimeScope(b =>
        {
            b.RegisterInstance<IOAuthClientRegistry>(provider).SingleInstance();

            if (groupHooks != null) b.RegisterInstance<IProviderRegistry>(new ProviderRegistry(new IProviderCapability[] { groupHooks })).SingleInstance();
        });

        return await delete.Resolve<IMediator>().Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId, Force = force }).ConfigureAwait(false);
    }

    /// <summary>The credential's payload as a connection outside any open transaction reads it — the committed state.</summary>
    private async Task<string> ReadCommittedPayloadAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.EncryptedPayload).SingleAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stands in for the provider's revocation endpoint: records each revoke with the credential's committed payload at
    /// that moment, and refuses every one when told to, as a provider that is down or rejects the client would.
    /// </summary>
    private sealed class RevocationRecorder : IOAuthClient, IOAuthClientRegistry
    {
        private readonly Func<Task<string>> _readCommittedPayload;

        public RevocationRecorder(Func<Task<string>> readCommittedPayload) { _readCommittedPayload = readCommittedPayload; }

        public List<(OAuthRevokeInput Input, string CommittedPayload)> Revokes { get; } = new();

        public bool Refuse { get; init; }

        public ProviderKind Kind => ProviderKind.GitHub;

        public IOAuthClient Get(ProviderKind kind) => this;

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken)
        {
            Revokes.Add((input, await _readCommittedPayload().ConfigureAwait(false)));

            if (Refuse) throw new HttpRequestException("stub provider: revocation refused");
        }
    }

    /// <summary>A group hook of the connection, registered by <paramref name="credentialId"/>, in <paramref name="status"/>.</summary>
    private async Task<Guid> SeedConnectionHookAsync(Guid instanceId, Guid credentialId, string ownerPath, RepositoryWebhookRegistrationStatus status, string? externalId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();

        db.ConnectionWebhook.Add(new ConnectionWebhook
        {
            Id = id,
            ProviderInstanceId = instanceId,
            CredentialId = credentialId,
            OwnerPath = ownerPath,
            ExternalId = externalId,
            CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}",
            SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt($"hook-secret-CANARY-{id:N}"),
            SubscribedEvents = new List<string> { "push" },
            RegistrationStatus = status
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return id;
    }

    private async Task<List<ConnectionWebhook>> LoadConnectionHooksAsync(Guid instanceId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().Where(w => w.ProviderInstanceId == instanceId).ToListAsync().ConfigureAwait(false);
    }

    /// <summary>The personal access token a sealed payload holds, or empty when the payload has been blanked.</summary>
    private string ReadPatToken(string encryptedPayload)
    {
        if (string.IsNullOrEmpty(encryptedPayload)) return string.Empty;

        using var scope = _fixture.BeginScope();
        var json = scope.Resolve<IPayloadEncryptor>().Decrypt(encryptedPayload);

        return scope.Resolve<ICredentialPayloadSerializer>().Deserialize<PatPayload>(AuthType.Pat, json).Token;
    }

    /// <summary>
    /// Stands in for the provider's group-hook endpoint: records each delete with the sealed payload of the credential
    /// the call presents at that moment, and refuses every one when told to, as a provider that is down would.
    ///
    /// <para>Parameterless and inert on purpose: the fixture's capability scan activates every capability class in this
    /// assembly into the container's registry, where the production GitHub provider, registered first, answers before
    /// it. Only the delete test's own registry reaches it.</para>
    /// </summary>
    private sealed class GroupHookRecorder : IConnectionWebhookRegistrationCapability
    {
        public List<(string OwnerPath, string ExternalId, string Payload)> Deletes { get; } = new();

        public bool Refuse { get; init; }

        public ProviderKind Kind => ProviderKind.GitHub;

        public Task<RemoteWebhook?> FindConnectionWebhookByCallbackUrlAsync(ProviderContext context, string ownerPath, string callbackUrl, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteWebhook> RegisterConnectionWebhookAsync(ProviderContext context, string ownerPath, WebhookRegistration request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteConnectionWebhookAsync(ProviderContext context, string ownerPath, string externalWebhookId, CancellationToken cancellationToken)
        {
            Deletes.Add((ownerPath, externalWebhookId, context.Credential.EncryptedPayload));

            return Refuse ? throw new HttpRequestException("stub provider: group hook delete refused") : Task.CompletedTask;
        }
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, Guid instanceId, Guid credentialId, string name = "api")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instanceId,
            CredentialId = credentialId,
            ExternalId = Guid.NewGuid().ToString("N"),
            NamespacePath = "acme",
            Name = name,
            FullPath = $"acme/{name}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = $"https://github.com/acme/{name}",
            Status = RepositoryStatus.Active
        };

        db.Repository.Add(repo);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return repo.Id;
    }
}
