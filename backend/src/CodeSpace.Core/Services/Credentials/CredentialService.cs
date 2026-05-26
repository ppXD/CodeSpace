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
        var granted = credential.Scopes ?? new List<string>();

        // No module = no declared requirements; surface every capability as available rather
        // than blocked so a future provider whose scope map hasn't been declared yet doesn't
        // grey out features in the SPA.
        if (module == null)
        {
            return new CredentialCapabilitiesResponse
            {
                CredentialId = credential.Id,
                GrantedScopes = granted,
                Capabilities = Array.Empty<CredentialCapabilityStatus>()
            };
        }

        var statuses = module.CapabilityScopeRequirements
            .Select(kvp =>
            {
                var outcome = _scopeChecker.Check(credential.ProviderInstance.Provider, kvp.Key, granted);
                return new CredentialCapabilityStatus
                {
                    Capability = kvp.Key.Name,
                    IsAvailable = outcome.IsSatisfied,
                    MissingScopes = outcome.MissingScopes
                };
            })
            .ToList();

        return new CredentialCapabilitiesResponse
        {
            CredentialId = credential.Id,
            GrantedScopes = granted,
            Capabilities = statuses
        };
    }

    public async Task<Guid> AddAsync(Guid providerInstanceId, Guid? ownerUserId, string displayName, CredentialPayload payload, CancellationToken cancellationToken)
    {
        var json = _serializer.Serialize(payload);
        var encrypted = _encryptor.Encrypt(json);

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = _currentTeam.Id!.Value,
            ProviderInstanceId = providerInstanceId,
            OwnerUserId = ownerUserId,
            AuthType = payload.Type,
            DisplayName = displayName,
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
}
