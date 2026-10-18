using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Services.Redaction;
using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Core.Services.Webhooks.Scope;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Dtos.ProviderInstances;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Queries.ProviderInstances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.ProviderInstances;

public sealed partial class ProviderInstanceService : IProviderInstanceService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly IPayloadEncryptor _encryptor;
    private readonly IRepositoryBindingService _binding;
    private readonly IWebhookScopeTransitionService _webhookScope;
    private readonly IConnectionWebhookProvisioner _connectionHooks;
    private readonly ICredentialRevocationService _revocation;
    private readonly IOAuthGrantRevoker _grants;
    private readonly IPostCommitActions _postCommit;
    private readonly IProviderEndpointProbe _endpointProbe;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProviderInstanceService> _logger;

    public ProviderInstanceService(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, IPayloadEncryptor encryptor, IRepositoryBindingService binding, IWebhookScopeTransitionService webhookScope, IConnectionWebhookProvisioner connectionHooks, ICredentialRevocationService revocation, IOAuthGrantRevoker grants, IPostCommitActions postCommit, IProviderEndpointProbe endpointProbe, TimeProvider clock, ILogger<ProviderInstanceService> logger)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _encryptor = encryptor;
        _binding = binding;
        _webhookScope = webhookScope;
        _connectionHooks = connectionHooks;
        _revocation = revocation;
        _grants = grants;
        _postCommit = postCommit;
        _endpointProbe = endpointProbe;
        _clock = clock;
        _logger = logger;
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
                // Masked: userinfo and secret-named query values are credentials, and this list reaches every member.
                // Update reads each exact string sent back as "keep the stored URL".
                BaseUrl = CredentialUrl.Masked(p.BaseUrl)!,
                ApiUrl = CredentialUrl.Masked(p.ApiUrl),
                WebUrl = CredentialUrl.Masked(p.WebUrl),
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

        EnsureOAuthPairConsistent(normalisedClientId, oauthClientSecret);
        await EnsureNotDuplicateAsync(teamId, provider, normalisedBaseUrl, normalisedClientId, cancellationToken).ConfigureAwait(false);
        await _endpointProbe.EnsureNoRedirectToAnotherOriginAsync(provider, ApiOf(normalisedBaseUrl, apiUrl), cancellationToken).ConfigureAwait(false);

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
        var edits = WithStoredUrlsKept(instance, request);

        // Compute the post-edit (base_url, client_id) tuple — needed for the uniqueness
        // check. Only run the check when at least one of those keys is actually changing,
        // because the no-op case would self-collide on the row's own values.
        var newBaseUrl = NextBaseUrl(instance, edits);
        var newClientId = edits.OauthClientId != null ? NormaliseClientId(edits.OauthClientId) : instance.OauthClientId;
        var baseUrlChanged = !string.Equals(newBaseUrl, instance.BaseUrl, StringComparison.Ordinal);
        var clientIdChanged = !string.Equals(newClientId, instance.OauthClientId, StringComparison.Ordinal);

        if (baseUrlChanged || clientIdChanged) await EnsureUniquenessKeyAvailableAsync(instance, newBaseUrl, newClientId, cancellationToken).ConfigureAwait(false);

        await EnsureSecretsStayWithTheirEndpointAsync(instance, edits, cancellationToken).ConfigureAwait(false);
        await EnsureAnUpgradeAnswersWhileSecretsAreBoundAsync(instance, edits, cancellationToken).ConfigureAwait(false);
        await EnsureAnEditedApiAddressDoesNotRedirectAsync(instance, edits, cancellationToken).ConfigureAwait(false);
        LogEndpointMove(instance, edits);

        var previousScope = instance.WebhookScope;

        ApplyEdits(instance, edits);

        await ApplyWebhookScopeChangeAsync(instance, previousScope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The edit with each URL the list shows MASKED (<see cref="CredentialUrl.Masked"/>) read back as "unchanged" when it
    /// is sent back exactly as shown, so an edit form or API client that PATCHes back what it GETs keeps the stored URL
    /// and its real embedded credentials; a placeholder left in an otherwise edited URL is refused. Resolved before the
    /// uniqueness check, so a masked echo of the row's own URL never reads as a different one.
    /// </summary>
    private static UpdateProviderInstanceCommand WithStoredUrlsKept(ProviderInstance instance, UpdateProviderInstanceCommand request) => request with
    {
        BaseUrl = request.BaseUrl == null ? null : CredentialUrl.KeepStoredIfMasked(instance.BaseUrl, NormaliseBaseUrl(request.BaseUrl)),
        ApiUrl = request.ApiUrl == null ? null : CredentialUrl.KeepStoredIfMasked(instance.ApiUrl, request.ApiUrl),
        WebUrl = request.WebUrl == null ? null : CredentialUrl.KeepStoredIfMasked(instance.WebUrl, request.WebUrl)
    };

    /// <summary>
    /// Whether an edit moves where this connection presents its secrets: the API every provider call goes to
    /// (<c>ApiUrl</c>, or the base URL when unset) or the base URL the OAuth token, authorize and revoke endpoints are
    /// derived from. A move is a change of scheme, host or port (<see cref="CredentialUrl.SameEndpoint"/>); a path, a
    /// trailing slash or a credential slot is not — and neither is an upgrade of http to https on the very same host
    /// (<see cref="CredentialUrl.IsHttpsUpgrade"/>), which a base URL that redirects to https asks for and which sends a
    /// token nowhere new, only over TLS: refusing it while tokens are connected left the owner no way to fix the URL
    /// short of disconnecting every member's token.
    /// </summary>
    internal static bool EndpointMoves(string storedBaseUrl, string? storedApiUrl, string nextBaseUrl, string? nextApiUrl) =>
        UrlMoves(storedBaseUrl, nextBaseUrl) || UrlMoves(ApiOf(storedBaseUrl, storedApiUrl), ApiOf(nextBaseUrl, nextApiUrl));

    private static bool UrlMoves(string stored, string next) => !CredentialUrl.SameEndpoint(stored, next) && !CredentialUrl.IsHttpsUpgrade(stored, next);

    /// <summary>Whether an edit changes the scheme, host or port of either URL at all — the moves, and the http to https upgrades that are allowed past bound credentials.</summary>
    internal static bool EndpointChanges(string storedBaseUrl, string? storedApiUrl, string nextBaseUrl, string? nextApiUrl) =>
        !CredentialUrl.SameEndpoint(storedBaseUrl, nextBaseUrl) || !CredentialUrl.SameEndpoint(ApiOf(storedBaseUrl, storedApiUrl), ApiOf(nextBaseUrl, nextApiUrl));

    /// <summary>
    /// Refuse a repoint the connection's secrets would follow. Its connected tokens are write-only and belong to the
    /// members who connected them, so an editor who may change the URL could otherwise point it at a host they control
    /// and have the next repository read, git node, webhook registration or OAuth refresh present every one of them
    /// there — they are disconnected first instead. The stored OAuth client secret is the editor's to re-enter, as a
    /// model key is. A connection with nothing bound moves freely.
    /// </summary>
    private async Task EnsureSecretsStayWithTheirEndpointAsync(ProviderInstance instance, UpdateProviderInstanceCommand edits, CancellationToken cancellationToken)
    {
        if (!EndpointMoves(instance.BaseUrl, instance.ApiUrl, NextBaseUrl(instance, edits), NextApiUrl(instance, edits))) return;

        var bound = await CountPresentableCredentialsAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        if (bound > 0)
            throw new ProviderEndpointCredentialsBoundException(instance.Id, bound);

        if (instance.OauthClientSecretEnc != null && string.IsNullOrEmpty(edits.OauthClientSecret))
            throw new ProviderEndpointSecretRequiredException(instance.Id);
    }

    /// <summary>
    /// An http to https upgrade is the one change of endpoint a connected token may follow, because it sends the token nowhere
    /// new; it can still land every connected token on an address nothing serves over TLS (a host with no https, a certificate
    /// this server does not trust), and putting the old address back is a move, refused while the tokens are connected — the
    /// owner would be stranded where the carve-out was meant to free them. So while any token is connected the new address
    /// must answer HTTP before the edit is kept, and the stored one stays otherwise. With nothing connected a mistaken address
    /// is one edit away from undone, so nothing is asked; an edit that relies on no upgrade asks nothing either.
    /// </summary>
    private async Task EnsureAnUpgradeAnswersWhileSecretsAreBoundAsync(ProviderInstance instance, UpdateProviderInstanceCommand edits, CancellationToken cancellationToken)
    {
        var upgraded = UpgradedAddresses(instance, edits);

        if (upgraded.Count == 0 || await CountPresentableCredentialsAsync(instance.Id, cancellationToken).ConfigureAwait(false) == 0) return;

        foreach (var address in upgraded) await _endpointProbe.EnsureAnswersAsync(instance.Provider, address, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The edit's next base URL and API address, each when it differs from the stored one by an http to https upgrade of the same host and nothing else (<see cref="CredentialUrl.IsHttpsUpgrade"/>).</summary>
    private static IReadOnlyList<string> UpgradedAddresses(ProviderInstance instance, UpdateProviderInstanceCommand edits)
    {
        var nextBaseUrl = NextBaseUrl(instance, edits);
        var addresses = new[] { (Stored: instance.BaseUrl, Next: nextBaseUrl), (Stored: ApiOf(instance.BaseUrl, instance.ApiUrl), Next: ApiOf(nextBaseUrl, NextApiUrl(instance, edits))) };

        return addresses.Where(address => CredentialUrl.IsHttpsUpgrade(address.Stored, address.Next)).Select(address => address.Next).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A new API address is asked once whether it redirects to another origin, so the redirect is named now, to the owner
    /// who typed it, instead of reading as a 401 on every call afterwards. After the refusals above, so a repoint that is
    /// refused never contacts the host it named. An edit that leaves the address as stored asks nothing — a connection
    /// whose stored URL already redirects keeps saving its other fields; its calls name the redirect when they meet it.
    /// </summary>
    private async Task EnsureAnEditedApiAddressDoesNotRedirectAsync(ProviderInstance instance, UpdateProviderInstanceCommand edits, CancellationToken cancellationToken)
    {
        var next = ApiOf(NextBaseUrl(instance, edits), NextApiUrl(instance, edits));

        if (string.Equals(next, ApiOf(instance.BaseUrl, instance.ApiUrl), StringComparison.Ordinal)) return;

        await _endpointProbe.EnsureNoRedirectToAnotherOriginAsync(instance.Provider, next, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Credentials that would still be presented: connected through this instance and not disconnected (a disconnect revokes the row and blanks its token).</summary>
    private Task<int> CountPresentableCredentialsAsync(Guid providerInstanceId, CancellationToken cancellationToken) =>
        _db.Credential.AsNoTracking().CountAsync(c => c.ProviderInstanceId == providerInstanceId && c.DeletedDate == null && c.Status != CredentialStatus.Revoked, cancellationToken);

    /// <summary>One audit line per endpoint change — a move, or an http to https upgrade — naming only scheme://host:port on each side (<see cref="CredentialUrl.Endpoint"/>) — never embedded credentials, path or query.</summary>
    private void LogEndpointMove(ProviderInstance instance, UpdateProviderInstanceCommand edits)
    {
        var nextBaseUrl = NextBaseUrl(instance, edits);
        var nextApiUrl = NextApiUrl(instance, edits);

        if (!EndpointChanges(instance.BaseUrl, instance.ApiUrl, nextBaseUrl, nextApiUrl)) return;

        _logger.LogInformation("Provider instance {ProviderInstanceId} of team {TeamId} moved its endpoint from {PreviousEndpoint} (API {PreviousApiEndpoint}) to {Endpoint} (API {ApiEndpoint}), by user {UserId}; an http to https upgrade of the same host: {HttpsUpgrade}", instance.Id, instance.TeamId, CredentialUrl.Endpoint(instance.BaseUrl), CredentialUrl.Endpoint(ApiOf(instance.BaseUrl, instance.ApiUrl)), CredentialUrl.Endpoint(nextBaseUrl), CredentialUrl.Endpoint(ApiOf(nextBaseUrl, nextApiUrl)), _currentUser.Id, !EndpointMoves(instance.BaseUrl, instance.ApiUrl, nextBaseUrl, nextApiUrl));
    }

    private static string NextBaseUrl(ProviderInstance instance, UpdateProviderInstanceCommand edits) => edits.BaseUrl != null ? NormaliseBaseUrl(edits.BaseUrl) : instance.BaseUrl;

    private static string? NextApiUrl(ProviderInstance instance, UpdateProviderInstanceCommand edits) => edits.ApiUrl == null ? instance.ApiUrl : NullIfBlank(edits.ApiUrl);

    /// <summary>Where provider calls go: the API URL when one is set, the base URL otherwise — the fallback every provider reads.</summary>
    private static string ApiOf(string baseUrl, string? apiUrl) => string.IsNullOrWhiteSpace(apiUrl) ? baseUrl : apiUrl;

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Deferred to after the edit commits, for the reason a bind defers its registration: the
    /// transition reads the new scope off the row and dispatches registrations against it, and a
    /// worker that got there first would read the old value and register the mode the operator just
    /// left. No-ops when the scope did not move, which is every ordinary edit.
    /// </summary>
    private async Task ApplyWebhookScopeChangeAsync(ProviderInstance instance, ProviderWebhookScope previousScope, CancellationToken cancellationToken)
    {
        if (instance.WebhookScope == previousScope) return;

        var instanceId = instance.Id;

        await _postCommit.RunAfterCommitAsync(ct => _webhookScope.ApplyAsync(instanceId, previousScope, ct), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeleteProviderInstanceResult> DeleteAsync(Guid providerInstanceId, bool force, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var instance = await LoadOwnedInstanceAsync(providerInstanceId, teamId, cancellationToken).ConfigureAwait(false);
        var unboundCount = await ResolveBoundRepositoriesAsync(instance, force, cancellationToken).ConfigureAwait(false);
        await RetireConnectionWebhooksAsync(instance, cancellationToken).ConfigureAwait(false);
        var revokedCount = await RetireCredentialsAsync(instance, cancellationToken).ConfigureAwait(false);

        instance.OauthClientSecretEnc = null;
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
    /// OAuth client ID + secret are all-or-nothing. Both present → a one-click-sign-in (OAuth)
    /// provider; both blank → a token-only provider (members connect with a personal access token,
    /// no OAuth app required). Exactly one is ambiguous (a client_id without a secret can't exchange
    /// a code, and a secret without an id is meaningless), so we reject it. Enforced backend-side so
    /// non-UI callers can't sidestep the frontend's validation either way.
    /// </summary>
    private static void EnsureOAuthPairConsistent(string? clientId, string? clientSecret)
    {
        var hasClientId = !string.IsNullOrWhiteSpace(clientId);
        var hasClientSecret = !string.IsNullOrWhiteSpace(clientSecret);

        if (hasClientId != hasClientSecret)
            throw new InvalidOperationException("Provide BOTH an OAuth client ID and secret to enable one-click sign-in, or leave BOTH blank to register a token-only provider (members connect with a personal access token).");
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
        if (request.WebhookScope != null) instance.WebhookScope = request.WebhookScope.Value;
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

    /// <summary>Empty / whitespace-only client IDs collapse to null so the form's "not yet configured" state and a literal empty string aren't treated as distinct identities by the unique index.</summary>
    private static string? NormaliseClientId(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>Trailing slash equivalence is the cheapest source of "looks unique but isn't" bugs — collapse it before the duplicate check.</summary>
    private static string NormaliseBaseUrl(string raw) => raw.TrimEnd('/');
}
