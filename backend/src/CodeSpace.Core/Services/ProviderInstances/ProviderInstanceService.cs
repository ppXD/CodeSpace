using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Dtos.ProviderInstances;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.ProviderInstances;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.ProviderInstances;

public sealed class ProviderInstanceService : IProviderInstanceService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly IPayloadEncryptor _encryptor;
    private readonly IRepositoryBindingService _binding;
    private readonly TimeProvider _clock;

    public ProviderInstanceService(CodeSpaceDbContext db, ICurrentTeam currentTeam, IPayloadEncryptor encryptor, IRepositoryBindingService binding, TimeProvider clock)
    {
        _db = db;
        _currentTeam = currentTeam;
        _encryptor = encryptor;
        _binding = binding;
        _clock = clock;
    }

    public async Task<IReadOnlyList<ProviderInstanceSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        return await _db.ProviderInstance
            .AsNoTracking()
            .Where(p => p.TeamId == teamId && p.DeletedDate == null)
            .OrderByDescending(p => p.CreatedDate)
            .Select(p => new ProviderInstanceSummary
            {
                Id = p.Id,
                TeamId = p.TeamId,
                Provider = p.Provider,
                DisplayName = p.DisplayName,
                BaseUrl = p.BaseUrl,
                ApiUrl = p.ApiUrl,
                WebUrl = p.WebUrl,
                CreatedDate = p.CreatedDate,
                OauthEnabled = p.OauthClientId != null && p.OauthClientSecretEnc != null
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderInstanceUsage> GetUsageAsync(Guid providerInstanceId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        // Two cheap counts. Tenancy is covered both by the IRequireTeamMembership pipeline
        // behavior AND a defensive WHERE on team_id (defence in depth against IDOR).
        var repoCount = await _db.Repository
            .AsNoTracking()
            .CountAsync(r => r.ProviderInstanceId == providerInstanceId && r.TeamId == teamId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        var credCount = await _db.Credential
            .AsNoTracking()
            .CountAsync(c => c.ProviderInstanceId == providerInstanceId && c.TeamId == teamId && c.DeletedDate == null && c.Status == CredentialStatus.Active, cancellationToken).ConfigureAwait(false);

        return new ProviderInstanceUsage
        {
            ProviderInstanceId = providerInstanceId,
            ActiveRepositoryCount = repoCount,
            ActiveCredentialCount = credCount
        };
    }

    public async Task<Guid> AddAsync(ProviderKind provider, string displayName, string baseUrl, string? apiUrl, string? webUrl, string? oauthClientId, string? oauthClientSecret, string? oauthRedirectPath, IReadOnlyList<string>? oauthDefaultScopes, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        var normalisedBaseUrl = NormaliseBaseUrl(baseUrl);
        var normalisedClientId = NormaliseClientId(oauthClientId);

        EnsureOAuthCredentialsProvided(normalisedClientId, oauthClientSecret);
        await EnsureNotDuplicateAsync(teamId, provider, normalisedBaseUrl, normalisedClientId, cancellationToken).ConfigureAwait(false);

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = provider,
            DisplayName = displayName,
            BaseUrl = normalisedBaseUrl,
            ApiUrl = apiUrl,
            WebUrl = webUrl,
            OauthClientId = normalisedClientId,
            OauthClientSecretEnc = oauthClientSecret != null ? _encryptor.Encrypt(oauthClientSecret) : null,
            OauthRedirectPath = oauthRedirectPath,
            OauthDefaultScopes = oauthDefaultScopes?.ToList()
        };

        await _db.ProviderInstance.AddAsync(instance, cancellationToken).ConfigureAwait(false);
        return instance.Id;
    }

    public async Task UpdateAsync(UpdateProviderInstanceCommand request, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var instance = await LoadOwnedInstanceAsync(request.ProviderInstanceId, teamId, cancellationToken).ConfigureAwait(false);

        // Compute the post-edit (base_url, client_id) tuple — needed for the uniqueness
        // check. Only run the check when at least one of those keys is actually changing,
        // because the no-op case would self-collide on the row's own values.
        var newBaseUrl = request.BaseUrl != null ? NormaliseBaseUrl(request.BaseUrl) : instance.BaseUrl;
        var newClientId = request.OauthClientId != null ? NormaliseClientId(request.OauthClientId) : instance.OauthClientId;
        var baseUrlChanged = !string.Equals(newBaseUrl, instance.BaseUrl, StringComparison.Ordinal);
        var clientIdChanged = !string.Equals(newClientId, instance.OauthClientId, StringComparison.Ordinal);

        if (baseUrlChanged || clientIdChanged) await EnsureUniquenessKeyAvailableAsync(instance, newBaseUrl, newClientId, cancellationToken).ConfigureAwait(false);

        ApplyEdits(instance, request);
    }

    public async Task<DeleteProviderInstanceResult> DeleteAsync(Guid providerInstanceId, bool force, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var instance = await LoadOwnedInstanceAsync(providerInstanceId, teamId, cancellationToken).ConfigureAwait(false);
        var unboundCount = await ResolveBoundRepositoriesAsync(instance, force, cancellationToken).ConfigureAwait(false);
        var revokedCount = await CascadeRevokeCredentialsAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        instance.DeletedDate = _clock.GetUtcNow();

        return new DeleteProviderInstanceResult
        {
            ProviderInstanceId = instance.Id,
            UnboundRepositoryCount = unboundCount,
            RevokedCredentialCount = revokedCount
        };
    }

    private async Task<ProviderInstance> LoadOwnedInstanceAsync(Guid id, Guid teamId, CancellationToken cancellationToken)
    {
        var instance = await _db.ProviderInstance.SingleOrDefaultAsync(p => p.Id == id && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Provider instance {id} not found");

        // TeamMembershipAuthorizationBehavior asserts the caller belongs to _currentTeam.
        // The extra (TeamId == teamId) guard here defends against IDOR.
        if (instance.TeamId != teamId) throw new KeyNotFoundException($"Provider instance {id} not found");

        return instance;
    }

    /// <summary>
    /// Mirrors partial unique index <c>idx_provider_instance_team_provider_url_client_active</c>:
    /// (team_id, provider, base_url, COALESCE(oauth_client_id, '')) WHERE deleted_date IS NULL.
    /// Two OAuth apps for the same host with different client IDs are intentionally allowed
    /// (high-scope admin app + narrow read-only app side by side). Pre-check so the operator
    /// gets a 400 with a friendly message instead of the constraint-violation 500.
    /// </summary>
    private async Task EnsureNotDuplicateAsync(Guid teamId, ProviderKind provider, string baseUrl, string? oauthClientId, CancellationToken cancellationToken)
    {
        var existing = await _db.ProviderInstance
            .AsNoTracking()
            .Where(p => p.TeamId == teamId && p.Provider == provider && p.BaseUrl == baseUrl && p.OauthClientId == oauthClientId && p.DeletedDate == null)
            .Select(p => new { p.Id, p.DisplayName })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing == null) return;

        var clientPart = oauthClientId == null
            ? " without an OAuth client ID"
            : $" with client ID '{oauthClientId}'";
        throw new InvalidOperationException($"A {provider} provider for '{baseUrl}'{clientPart} already exists in this team ('{existing.DisplayName}'). Edit or remove that one, or use a different OAuth client ID.");
    }

    /// <summary>
    /// A provider instance without OAuth credentials is a half-state row the UI can't act on
    /// (no Connect possible). Enforced backend-side so non-UI callers can't sidestep the
    /// frontend's required-field validation.
    /// </summary>
    private static void EnsureOAuthCredentialsProvided(string? clientId, string? clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("OAuth client ID and client secret are required when adding a provider. Register an OAuth application on the provider first, then come back with the credentials.");
    }

    private async Task EnsureUniquenessKeyAvailableAsync(ProviderInstance instance, string newBaseUrl, string? newClientId, CancellationToken cancellationToken)
    {
        var clash = await _db.ProviderInstance
            .AsNoTracking()
            .AnyAsync(p => p.TeamId == instance.TeamId && p.Provider == instance.Provider && p.BaseUrl == newBaseUrl && p.OauthClientId == newClientId && p.DeletedDate == null && p.Id != instance.Id, cancellationToken).ConfigureAwait(false);

        if (!clash) return;

        var clientPart = newClientId == null
            ? " without an OAuth client ID"
            : $" with client ID '{newClientId}'";
        throw new InvalidOperationException($"Another {instance.Provider} provider for '{newBaseUrl}'{clientPart} already exists in this team. Pick a different base URL or OAuth client ID.");
    }

    /// <summary>
    /// PATCH semantics — null = leave alone. Empty string on the OAuth secret is treated as
    /// "no rotate" because that's the form's natural state when the user doesn't intend to
    /// change a stored secret.
    /// </summary>
    private void ApplyEdits(ProviderInstance instance, UpdateProviderInstanceCommand request)
    {
        if (request.DisplayName != null) instance.DisplayName = request.DisplayName;
        if (request.BaseUrl != null) instance.BaseUrl = NormaliseBaseUrl(request.BaseUrl);
        if (request.ApiUrl != null) instance.ApiUrl = string.IsNullOrWhiteSpace(request.ApiUrl) ? null : request.ApiUrl;
        if (request.WebUrl != null) instance.WebUrl = string.IsNullOrWhiteSpace(request.WebUrl) ? null : request.WebUrl;
        if (request.OauthClientId != null) instance.OauthClientId = string.IsNullOrWhiteSpace(request.OauthClientId) ? null : request.OauthClientId;
        if (!string.IsNullOrEmpty(request.OauthClientSecret)) instance.OauthClientSecretEnc = _encryptor.Encrypt(request.OauthClientSecret);
        if (request.OauthRedirectPath != null) instance.OauthRedirectPath = string.IsNullOrWhiteSpace(request.OauthRedirectPath) ? null : request.OauthRedirectPath;
        if (request.OauthDefaultScopes != null) instance.OauthDefaultScopes = request.OauthDefaultScopes.ToList();
    }

    /// <summary>
    /// Force=false → count active repos and refuse if any (operator must unbind deliberately).
    /// Force=true  → cascade-unbind every active repo via RepositoryBindingService. UnbindAsync
    /// does best-effort remote-webhook delete + local soft-delete; credentials are still Active
    /// at this point so the webhook DELETE has a working token. Order matters: unbind before
    /// revoking credentials in the next step.
    /// </summary>
    private async Task<int> ResolveBoundRepositoriesAsync(ProviderInstance instance, bool force, CancellationToken cancellationToken)
    {
        var activeRepoIds = await _db.Repository
            .AsNoTracking()
            .Where(r => r.ProviderInstanceId == instance.Id && r.DeletedDate == null)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (activeRepoIds.Count == 0) return 0;

        if (!force)
            throw new InvalidOperationException($"Cannot remove {instance.Provider} provider '{instance.DisplayName}' while {activeRepoIds.Count} repositor{(activeRepoIds.Count == 1 ? "y is" : "ies are")} still bound to it. Unbind those repositories first, or remove the provider with the 'unbind all' option.");

        foreach (var repoId in activeRepoIds)
        {
            // The provider instance is being removed — drop these repos entirely (all project links),
            // not per-project. projectId: null = full teardown.
            await _binding.UnbindAsync(repoId, null, cancellationToken).ConfigureAwait(false);
        }

        return activeRepoIds.Count;
    }

    private async Task<int> CascadeRevokeCredentialsAsync(Guid providerInstanceId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        var activeCredentials = await _db.Credential
            .Where(c => c.ProviderInstanceId == providerInstanceId && c.DeletedDate == null && c.Status == CredentialStatus.Active)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var credential in activeCredentials)
        {
            credential.Status = CredentialStatus.Revoked;
            credential.DeletedDate = now;
            credential.LastError = "Provider instance deleted";
        }

        return activeCredentials.Count;
    }

    /// <summary>Empty / whitespace-only client IDs collapse to null so the form's "not yet configured" state and a literal empty string aren't treated as distinct identities by the unique index.</summary>
    private static string? NormaliseClientId(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>Trailing slash equivalence is the cheapest source of "looks unique but isn't" bugs — collapse it before the duplicate check.</summary>
    private static string NormaliseBaseUrl(string raw) => raw.TrimEnd('/');
}
