using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Core.Settings.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.OAuth;

public sealed class OAuthFlowService : IOAuthFlowService, IScopedDependency
{
    /// <summary>How long a pending-state row stays consumable. Long enough for the user to
    /// authorise on the provider page; short enough that an abandoned popup doesn't leave
    /// recoverable state lying around indefinitely.</summary>
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    private readonly CodeSpaceDbContext _db;
    private readonly IOAuthClientRegistry _oauthClients;
    private readonly IOAuthStateStore _stateStore;
    private readonly IPkceGenerator _pkce;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly IUserProviderIdentityService _identityService;
    private readonly ICredentialSuccessionService _succession;
    private readonly OAuthCallbackUrlSetting _callbackUrlSetting;
    private readonly PublicBaseUrlSetting _publicBaseUrl;

    public OAuthFlowService(CodeSpaceDbContext db, IOAuthClientRegistry oauthClients, IOAuthStateStore stateStore, IPkceGenerator pkce, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, ICurrentTeam currentTeam, ICurrentUser currentUser, IUserProviderIdentityService identityService, ICredentialSuccessionService succession, OAuthCallbackUrlSetting callbackUrlSetting, PublicBaseUrlSetting publicBaseUrl)
    {
        _db = db;
        _oauthClients = oauthClients;
        _stateStore = stateStore;
        _pkce = pkce;
        _encryptor = encryptor;
        _serializer = serializer;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _identityService = identityService;
        _succession = succession;
        _callbackUrlSetting = callbackUrlSetting;
        _publicBaseUrl = publicBaseUrl;
    }

    public async Task<InitCredentialOAuthResult> InitAsync(Guid providerInstanceId, string displayName, Guid? intendedOwnerUserId, string? returnUrl, IReadOnlyList<string>? scopes, CancellationToken cancellationToken)
    {
        EnsureReturnUrlAllowed(returnUrl);

        var instance = await LoadOwnedInstanceAsync(providerInstanceId, cancellationToken).ConfigureAwait(false);
        var (clientId, _) = ReadOAuthClientCredentials(instance);

        var pkce = _pkce.Generate();
        var browserNonce = OAuthBrowserNonce.Mint();

        var pending = await _stateStore.CreateAsync(new OAuthPendingStateInput
        {
            ProviderInstanceId = instance.Id,
            TeamId = _currentTeam.Id!.Value,
            InitiatorUserId = _currentUser.Id!.Value,
            CodeVerifier = pkce.Verifier,
            IntendedDisplayName = displayName,
            IntendedOwnerUserId = intendedOwnerUserId ?? _currentUser.Id!.Value,
            ReturnUrl = returnUrl,
            BrowserNonceHash = OAuthBrowserNonce.Hash(browserNonce),
            RequestedScopes = scopes ?? instance.OauthDefaultScopes,
            Ttl = StateTtl
        }, cancellationToken).ConfigureAwait(false);

        var client = _oauthClients.Get(instance.Provider);

        var authorizeUrl = client.BuildAuthorizeUrl(new OAuthAuthorizeInput
        {
            Instance = instance,
            ClientId = clientId,
            State = pending.State,
            CodeChallengeS256 = pkce.ChallengeS256,
            RedirectUri = ResolveCallbackUri(),
            Scopes = pending.RequestedScopes
        });

        return new InitCredentialOAuthResult { AuthorizeUrl = authorizeUrl, State = pending.State, BrowserNonce = browserNonce, Lifetime = StateTtl, BrowserNonceCookiePath = ResolveCallbackUri().AbsolutePath };
    }

    public async Task<CompleteCredentialOAuthResult> CompleteAsync(string state, string code, string? browserNonce, CancellationToken cancellationToken)
    {
        var pending = await ConsumeStateAsync(state, browserNonce, cancellationToken).ConfigureAwait(false);
        var instance = await LoadInstanceAsync(pending.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
        var (clientId, clientSecret) = ReadOAuthClientCredentials(instance);

        var token = await ExchangeCodeAsync(instance, clientId, clientSecret, code, pending.CodeVerifier, cancellationToken).ConfigureAwait(false);
        var credential = PersistCredential(pending, instance, token);

        await _succession.CarryForwardAsync(credential, cancellationToken).ConfigureAwait(false);

        if (pending.IntendedOwnerUserId is Guid ownerId)
            await LinkActorIdentityBestEffortAsync(instance, credential, ownerId, cancellationToken).ConfigureAwait(false);

        return new CompleteCredentialOAuthResult
        {
            CredentialId = credential.Id,
            TeamId = pending.TeamId,
            // Checked again rather than trusted from init: the public origin can change while a connect is pending.
            ReturnUrl = OAuthReturnUrl.AllowedOrFallback(pending.ReturnUrl, _publicBaseUrl.Value),
            BrowserNonceCookiePath = ResolveCallbackUri().AbsolutePath
        };
    }

    /// <summary>
    /// Refused before a flow starts, so a connect can never be set up to end on someone else's page. The address
    /// comes from whoever starts the connect, and the browser that follows it has just come from a consent screen
    /// that made the whole trip look legitimate.
    /// </summary>
    private void EnsureReturnUrlAllowed(string? returnUrl)
    {
        if (returnUrl == null || OAuthReturnUrl.IsAllowed(returnUrl, _publicBaseUrl.Value)) return;

        throw new ArgumentException("returnUrl must be a path on this site or an address on its public origin.", nameof(returnUrl));
    }

    /// <summary>
    /// Give the freshly-connected OAuth credential an act-as-user identity, so "act as me" features
    /// (e.g. PR review-as-me) work for OAuth users, not just PAT users. Best-effort: a probe hiccup
    /// right after a valid token exchange must NOT fail the whole connect — the credential is already
    /// valid for repo operations, and the user can reconnect to retry the identity link.
    /// </summary>
    private async Task LinkActorIdentityBestEffortAsync(ProviderInstance instance, Credential credential, Guid ownerId, CancellationToken cancellationToken)
    {
        try
        {
            await _identityService.EnsureIdentityForCredentialAsync(instance, credential, ownerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[OAuthFlow] Connected credential {CredentialId} but could not link an act-as-user identity for user {UserId}; act-as-me stays unavailable until reconnect", credential.Id, ownerId);
        }
    }

    private async Task<ProviderInstance> LoadOwnedInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _db.ProviderInstance.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new OAuthCallbackException($"provider instance {id} not found");

        if (instance.TeamId != _currentTeam.Id!.Value) throw new OAuthCallbackException("provider instance does not belong to the current team");

        return instance;
    }

    // Complete-internals (callback runs anonymous, no current-team check).

    /// <summary>
    /// Consumes the state only together with the nonce of the browser that started the flow. A callback from any
    /// other browser — the person an authorize link was forwarded to, or nobody at all — matches no row, so nothing
    /// is consumed, exchanged or persisted and the starting browser can still finish. One message for every refusal:
    /// telling "wrong browser" from "no such state" would only confirm which states are live.
    /// </summary>
    private async Task<OAuthPendingState> ConsumeStateAsync(string state, string? browserNonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(browserNonce))
            throw UnusableState();

        var row = await _stateStore.ConsumeAsync(state, OAuthBrowserNonce.Hash(browserNonce), cancellationToken).ConfigureAwait(false);

        return row ?? throw UnusableState();
    }

    private static OAuthCallbackException UnusableState() => new("state token missing, expired, already consumed, or not started in this browser");

    private async Task<ProviderInstance> LoadInstanceAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.ProviderInstance.FirstOrDefaultAsync(p => p.Id == id && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new OAuthCallbackException($"provider instance {id} no longer exists");

    private async Task<OAuthTokenResponse> ExchangeCodeAsync(ProviderInstance instance, string clientId, string clientSecret, string code, string verifier, CancellationToken cancellationToken)
    {
        var client = _oauthClients.Get(instance.Provider);

        return await client.ExchangeCodeAsync(new OAuthCodeExchangeInput
        {
            Instance = instance,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Code = code,
            CodeVerifier = verifier,
            RedirectUri = ResolveCallbackUri()
        }, cancellationToken).ConfigureAwait(false);
    }

    private Credential PersistCredential(OAuthPendingState state, ProviderInstance instance, OAuthTokenResponse token)
    {
        var payload = new OAuthPayload
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresAt
        };

        var json = _serializer.Serialize(payload);
        var encrypted = _encryptor.Encrypt(json);

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = state.TeamId,
            ProviderInstanceId = state.ProviderInstanceId,
            OwnerUserId = state.IntendedOwnerUserId,
            AuthType = AuthType.OAuth,
            DisplayName = state.IntendedDisplayName,
            EncryptedPayload = encrypted,
            Scopes = token.GrantedScopes?.ToList(),
            ExpiresDate = token.ExpiresAt,
            Status = CredentialStatus.Active,
            // Attribute to the original initiator, not the anonymous callback identity —
            // honored by the DbContext audit pipeline because we set this pre-save.
            CreatedBy = state.InitiatorUserId,
            LastModifiedBy = state.InitiatorUserId
        };

        _db.Credential.Add(credential);
        return credential;
    }

    private (string ClientId, string ClientSecret) ReadOAuthClientCredentials(ProviderInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.OauthClientId) || string.IsNullOrWhiteSpace(instance.OauthClientSecretEnc))
            throw new OAuthCallbackException($"provider instance {instance.Id} is not OAuth-configured (missing client_id or client_secret)");

        var clientSecret = _encryptor.Decrypt(instance.OauthClientSecretEnc);
        return (instance.OauthClientId, clientSecret);
    }

    private Uri ResolveCallbackUri()
    {
        if (string.IsNullOrWhiteSpace(_callbackUrlSetting.Value))
            throw new InvalidOperationException("OAuth:CallbackUrl is not configured. Set the absolute URL of /api/credentials/oauth/callback that providers will redirect to.");

        return new Uri(_callbackUrlSetting.Value);
    }
}
