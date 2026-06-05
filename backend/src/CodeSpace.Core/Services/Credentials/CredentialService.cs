using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed class CredentialService : ICredentialService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;
    private readonly IOAuthClientRegistry _oauthClients;
    private readonly IProviderRegistry _registry;
    private readonly IProviderModuleCatalog _modules;
    private readonly IScopeChecker _scopeChecker;
    private readonly ILogger<CredentialService> _logger;

    public CredentialService(CodeSpaceDbContext db, ICurrentTeam currentTeam, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, IOAuthClientRegistry oauthClients, IProviderRegistry registry, IProviderModuleCatalog modules, IScopeChecker scopeChecker, ILogger<CredentialService> logger)
    {
        _db = db;
        _currentTeam = currentTeam;
        _encryptor = encryptor;
        _serializer = serializer;
        _oauthClients = oauthClients;
        _registry = registry;
        _modules = modules;
        _scopeChecker = scopeChecker;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CredentialSummary>> ListAsync(Guid? providerInstanceId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        var query = _db.Credential.AsNoTracking().Where(c => c.TeamId == teamId && c.DeletedDate == null);

        if (providerInstanceId.HasValue) query = query.Where(c => c.ProviderInstanceId == providerInstanceId.Value);

        // Left-join the owning user so the UI can show "alice's GitHub · alice" in the Add
        // Repository picker. Two credentials with the same DisplayName (e.g. after a rename)
        // would otherwise be ambiguous; OwnerUserName disambiguates without forcing the user
        // to think about display-name uniqueness.
        return await query
            .OrderByDescending(c => c.CreatedDate)
            .Select(c => new CredentialSummary
            {
                Id = c.Id,
                TeamId = c.TeamId,
                ProviderInstanceId = c.ProviderInstanceId,
                OwnerUserId = c.OwnerUserId,
                OwnerUserName = c.Owner != null ? c.Owner.Name : null,
                Ownership = c.Ownership,
                AuthType = c.AuthType,
                DisplayName = c.DisplayName,
                Status = c.Status,
                ExpiresDate = c.ExpiresDate,
                LastValidatedDate = c.LastValidatedDate,
                LastError = c.LastError,
                CreatedDate = c.CreatedDate
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CredentialUsage> GetUsageAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        // Active = not soft-deleted AND not already flipped to Error by a prior revoke.
        // Counting only Status=Active gives the right "you're about to break N working
        // repositories" number; previously-broken ones don't need to be in the warning.
        var activeCount = await _db.Repository
            .AsNoTracking()
            .Where(r => r.CredentialId == credentialId && r.DeletedDate == null && r.Status == RepositoryStatus.Active)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        return new CredentialUsage { CredentialId = credentialId, ActiveRepositoryCount = activeCount };
    }

    public async Task<CredentialCapabilitiesResponse> GetCapabilitiesAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var credential = await _db.Credential
            .AsNoTracking()
            .Include(c => c.ProviderInstance)
            .SingleOrDefaultAsync(c => c.Id == credentialId && c.TeamId == teamId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Credential {credentialId} not found");

        var module = _modules.Get(credential.ProviderInstance.Provider);

        // No declared scope map → nothing to report. Surface no capabilities rather than blocking a
        // provider whose requirements haven't been declared yet.
        if (module == null) return BuildCapabilitiesResponse(credential, Array.Empty<CredentialCapabilityStatus>());

        var granted = credential.Scopes;

        // granted == null means we never captured this token's scopes (a PAT linked before scope-
        // capture existed, or a token type that can't expose them). null is UNKNOWN, not "zero
        // scopes" — surface every capability as available so we never show a FALSE "missing"
        // warning. Only a known scope list (incl. an explicitly empty one) is checked and can warn.
        if (granted == null)
            return BuildCapabilitiesResponse(credential, module.CapabilityScopeRequirements.Select(kvp => Available(kvp.Key)).ToList());

        var statuses = module.CapabilityScopeRequirements
            .Select(kvp => ToStatus(kvp.Key, _scopeChecker.Check(credential.ProviderInstance.Provider, kvp.Key, granted)))
            .ToList();

        return BuildCapabilitiesResponse(credential, statuses);
    }

    private static CredentialCapabilityStatus ToStatus(Type capability, ScopeCheckOutcome outcome) =>
        new() { Capability = capability.Name, IsAvailable = outcome.IsSatisfied, MissingScopes = outcome.MissingScopes };

    private static CredentialCapabilityStatus Available(Type capability) =>
        new() { Capability = capability.Name, IsAvailable = true };

    private static CredentialCapabilitiesResponse BuildCapabilitiesResponse(Credential credential, IReadOnlyList<CredentialCapabilityStatus> capabilities) =>
        new()
        {
            CredentialId = credential.Id,
            GrantedScopes = credential.Scopes ?? new List<string>(),
            Capabilities = capabilities
        };

    public async Task<Guid> AddAsync(AddCredentialInput input, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        // Tenant invariant: the target provider instance MUST belong to the caller's team. The command
        // only gates team MEMBERSHIP (IRequireTeamMembership), not the instance — so without this a team
        // member could attach a credential to ANOTHER team's instance by passing a foreign
        // ProviderInstanceId, leaving a credential whose ProviderInstance navigation crosses the tenant
        // boundary (a confused-deputy seam: the platform would pair that instance's host + OAuth client
        // with this team's token). Conflate not-found with not-in-team — a 404 leaks nothing about
        // another team's instances.
        await EnsureProviderInstanceInTeamAsync(input.ProviderInstanceId, teamId, cancellationToken).ConfigureAwait(false);

        var json = _serializer.Serialize(input.Payload);
        var encrypted = _encryptor.Encrypt(json);

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = input.ProviderInstanceId,
            // A team-service credential belongs to the team, not a person — never carry an owner.
            OwnerUserId = input.Ownership == CredentialOwnership.TeamService ? null : input.OwnerUserId,
            Ownership = input.Ownership,
            AuthType = input.Payload.Type,
            DisplayName = input.DisplayName,
            EncryptedPayload = encrypted,
            Status = CredentialStatus.Active
        };

        await _db.Credential.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        return credential.Id;
    }

    public async Task<RevokeCredentialResult> RevokeAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);

        var alreadyRevoked = credential.Status == CredentialStatus.Revoked;

        // Skip provider revoke + local-state update when already Revoked — the token is gone,
        // nothing to revoke. ALWAYS run the cascade below: it's the only path that heals an
        // out-of-sync Repository row (Active pointing at a Revoked credential), which can
        // happen if the cascade didn't exist when the credential was first revoked. The
        // Status != Error filter inside MarkDependentRepositoriesUnauthorisedAsync makes the
        // call idempotent.
        bool providerOk = true;
        string? providerError = null;

        if (!alreadyRevoked)
        {
            (providerOk, providerError) = await TryRevokeAtProviderAsync(credential, cancellationToken).ConfigureAwait(false);
            MarkRevokedLocally(credential, providerError);
        }

        var affectedRepoCount = await MarkDependentRepositoriesUnauthorisedAsync(credential.Id, cancellationToken).ConfigureAwait(false);
        await SoftDeleteDependentIdentitiesAsync(credential.Id, cancellationToken).ConfigureAwait(false);

        return new RevokeCredentialResult
        {
            CredentialId = credential.Id,
            ProviderAcknowledged = providerOk,
            ProviderError = providerError,
            AffectedRepositoryCount = affectedRepoCount
        };
    }

    public async Task<RemoteRepositoryPage> ListAccessibleRepositoriesAsync(Guid credentialId, string? search, int page, int perPage, CancellationToken cancellationToken)
    {
        var credential = await _db.Credential
            .Include(c => c.ProviderInstance)
            .SingleOrDefaultAsync(c => c.Id == credentialId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credential {credentialId} not found");

        var catalog = _registry.Require<IRepositoryCatalogCapability>(credential.ProviderInstance.Provider);
        var context = new ProviderContext(credential.ProviderInstance, credential);

        // Clamp untrusted inputs — Page 0/-1 would 422 on GitHub; PerPage > 100 silently
        // degrades. Same pattern as the PR-list clamps.
        var clampedPage = page < 1 ? 1 : page;
        var clampedPerPage = Math.Clamp(perPage, 1, ListAccessibleRepositoriesQuery.MaxPerPage);

        return await catalog.PagedListAccessibleAsync(context, search, clampedPage, clampedPerPage, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureProviderInstanceInTeamAsync(Guid providerInstanceId, Guid teamId, CancellationToken cancellationToken)
    {
        var inTeam = await _db.ProviderInstance.AsNoTracking()
            .AnyAsync(p => p.Id == providerInstanceId && p.TeamId == teamId && p.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (!inTeam) throw new KeyNotFoundException($"Provider instance {providerInstanceId} not found.");
    }

    private async Task<Credential> LoadCredentialAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.Credential.Include(c => c.ProviderInstance).FirstOrDefaultAsync(c => c.Id == id && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"credential {id} not found");

    private async Task<(bool Ok, string? Error)> TryRevokeAtProviderAsync(Credential credential, CancellationToken cancellationToken)
    {
        // Non-OAuth credentials (PAT, Project/Group AT, SSH key, Basic) have nothing to revoke
        // at the provider — only the user deleting it on their side counts.
        if (credential.AuthType != AuthType.OAuth) return (Ok: true, Error: null);

        if (string.IsNullOrWhiteSpace(credential.ProviderInstance.OauthClientId) || string.IsNullOrWhiteSpace(credential.ProviderInstance.OauthClientSecretEnc))
            return (Ok: false, Error: "provider instance missing OAuth client credentials");

        var payload = TryReadOAuthPayload(credential);

        if (payload == null) return (Ok: false, Error: "credential payload is not a valid OAuth payload");

        try
        {
            await RevokeOAuthTokensAsync(credential.ProviderInstance, payload, cancellationToken).ConfigureAwait(false);
            return (Ok: true, Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider revoke failed for credential {CredentialId}; clearing local payload anyway", credential.Id);
            return (Ok: false, Error: ex.Message);
        }
    }

    private OAuthPayload? TryReadOAuthPayload(Credential credential)
    {
        try
        {
            var json = _encryptor.Decrypt(credential.EncryptedPayload);
            return _serializer.Deserialize(AuthType.OAuth, json) as OAuthPayload;
        }
        catch
        {
            return null;
        }
    }

    private async Task RevokeOAuthTokensAsync(ProviderInstance instance, OAuthPayload payload, CancellationToken cancellationToken)
    {
        var client = _oauthClients.Get(instance.Provider);
        var clientSecret = _encryptor.Decrypt(instance.OauthClientSecretEnc!);

        // Revoke refresh_token first (more durable — kills the grant on most providers), then
        // access_token. Both are idempotent on the provider side; the caller's catch clears
        // local payload regardless.
        if (!string.IsNullOrEmpty(payload.RefreshToken))
        {
            await client.RevokeAsync(new OAuthRevokeInput
            {
                Instance = instance,
                ClientId = instance.OauthClientId!,
                ClientSecret = clientSecret,
                Token = payload.RefreshToken,
                TokenTypeHint = "refresh_token"
            }, cancellationToken).ConfigureAwait(false);
        }

        await client.RevokeAsync(new OAuthRevokeInput
        {
            Instance = instance,
            ClientId = instance.OauthClientId!,
            ClientSecret = clientSecret,
            Token = payload.AccessToken,
            TokenTypeHint = "access_token"
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void MarkRevokedLocally(Credential credential, string? providerError)
    {
        // Clear encrypted payload — even if the provider call failed, removing token material
        // from our DB ensures nothing in CodeSpace can present it for new requests.
        credential.EncryptedPayload = string.Empty;
        credential.Status = CredentialStatus.Revoked;
        credential.LastError = providerError;
        credential.LastValidatedDate = null;
    }

    /// <summary>
    /// Repositories bound through this credential lose their auth source the moment it's
    /// revoked. We DON'T cascade-delete them (too destructive — disconnect is often part of
    /// token rotation, and the user expects to come back), but we DO flip status to
    /// <see cref="RepositoryStatus.Error"/> with a concrete remediation message so the UI
    /// can show "Needs new credential" instead of pretending the repo is still healthy.
    ///
    /// Webhook ingestion is deliberately untouched — webhooks authenticate via the secret we
    /// registered, not via the OAuth token. Events keep flowing in, and once the user
    /// re-links to a new credential the repo is fully healthy again with no event gap.
    /// </summary>
    private async Task<int> MarkDependentRepositoriesUnauthorisedAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var affected = await _db.Repository
            .Where(r => r.CredentialId == credentialId && r.DeletedDate == null && r.Status != RepositoryStatus.Error)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var repo in affected)
        {
            repo.Status = RepositoryStatus.Error;
            repo.LastError = "Credential disconnected. Re-link this repository to an active credential of the same provider, or unbind it.";
        }

        return affected.Count;
    }

    /// <summary>
    /// A <see cref="UserProviderIdentity"/> (Model B act-as-user link) is unusable without its backing
    /// credential, so revoking the credential — e.g. Disconnect on the Personal tab — must soft-delete the
    /// linked identity too. Otherwise the Connected Identities surface keeps showing a dead link, out of
    /// sync with the Personal tab. This mirrors what <c>UserProviderIdentityService.UnlinkAsync</c> does
    /// from the identity side; tracked + saved by the same unit of work as the revoke, and idempotent
    /// (no live identity ⇒ no-op) so the always-run cascade is safe on an already-revoked credential.
    /// </summary>
    private async Task SoftDeleteDependentIdentitiesAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var identities = await _db.UserProviderIdentity
            .Where(i => i.CredentialId == credentialId && i.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var identity in identities) identity.DeletedDate = now;
    }
}
