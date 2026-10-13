using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Services.Teams;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Core.Settings.CorsPolicy;
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
    private readonly ITeamMemberService _members;
    private readonly OAuthCallbackUrlSetting _callbackUrlSetting;
    private readonly OAuthReturnOriginsSetting _returnOrigins;

    public OAuthFlowService(CodeSpaceDbContext db, IOAuthClientRegistry oauthClients, IOAuthStateStore stateStore, IPkceGenerator pkce, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, ICurrentTeam currentTeam, ICurrentUser currentUser, IUserProviderIdentityService identityService, ICredentialSuccessionService succession, ITeamMemberService members, OAuthCallbackUrlSetting callbackUrlSetting, OAuthReturnOriginsSetting returnOrigins)
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
        _members = members;
        _callbackUrlSetting = callbackUrlSetting;
        _returnOrigins = returnOrigins;
    }

    public async Task<InitCredentialOAuthResult> InitAsync(Guid providerInstanceId, string displayName, Guid? intendedOwnerUserId, string? returnUrl, IReadOnlyList<string>? scopes, CancellationToken cancellationToken)
    {
        EnsureReturnUrlAllowed(returnUrl);

        var instance = await LoadOwnedInstanceAsync(providerInstanceId, cancellationToken).ConfigureAwait(false);
        var (clientId, _) = ReadOAuthClientCredentials(instance);
        var ownerUserId = await ResolveOwnerWithinReachAsync(intendedOwnerUserId, cancellationToken).ConfigureAwait(false);

        var pkce = _pkce.Generate();
        var browserNonce = OAuthBrowserNonce.Mint();

        var pending = await _stateStore.CreateAsync(new OAuthPendingStateInput
        {
            ProviderInstanceId = instance.Id,
            TeamId = _currentTeam.Id!.Value,
            InitiatorUserId = _currentUser.Id!.Value,
            CodeVerifier = pkce.Verifier,
            IntendedDisplayName = displayName,
            IntendedOwnerUserId = ownerUserId,
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
        var pending = await ConsumeBrowserBoundStateAsync(state, browserNonce, cancellationToken).ConfigureAwait(false);

        if (pending == null) return await HandOffToInitiatorAsync(state, cancellationToken).ConfigureAwait(false);

        return await FinishAsync(pending, code, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompleteCredentialOAuthResult> CompleteAsInitiatorAsync(string state, string code, CancellationToken cancellationToken)
    {
        var pending = await ConsumeInitiatorBoundStateAsync(state, cancellationToken).ConfigureAwait(false);

        return await FinishAsync(pending, code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exchanges the code for the consumed state and keeps the token — whichever proof consumed it.</summary>
    private async Task<CompleteCredentialOAuthResult> FinishAsync(OAuthPendingState pending, string code, CancellationToken cancellationToken)
    {
        var instance = await LoadInstanceAsync(pending.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
        var (clientId, clientSecret) = ReadOAuthClientCredentials(instance);

        await EnsureOwnerStillInTeamAsync(pending, cancellationToken).ConfigureAwait(false);

        var token = await ExchangeCodeAsync(instance, clientId, clientSecret, code, pending.CodeVerifier, cancellationToken).ConfigureAwait(false);

        await HoldOwnerInTeamAsync(pending, cancellationToken).ConfigureAwait(false);

        var credential = PersistCredential(pending, instance, token);

        await _succession.CarryForwardAsync(credential, cancellationToken).ConfigureAwait(false);

        if (pending.IntendedOwnerUserId is Guid ownerId)
            await LinkActorIdentityBestEffortAsync(instance, credential, ownerId, cancellationToken).ConfigureAwait(false);

        return new CompleteCredentialOAuthResult
        {
            CredentialId = credential.Id,
            TeamId = pending.TeamId,
            // Checked again rather than trusted from init: the configured origins can change while a connect is pending.
            ReturnUrl = OAuthReturnUrl.AllowedOrFallback(pending.ReturnUrl, _returnOrigins.Value),
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
        if (returnUrl == null || OAuthReturnUrl.IsAllowed(returnUrl, _returnOrigins.Value)) return;

        throw new ArgumentException($"returnUrl must be a path on this site, or an address on an origin this deployment serves its web app from — {PublicBaseUrlSetting.ConfigurationKey}, or one listed in {AllowableCorsOriginsSetting.ConfigurationKey}.", nameof(returnUrl));
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

    /// <summary>
    /// Who the credential will belong to — the caller unless they name someone — and whom an OAuth connect links as an
    /// act-as identity. Naming them is acting on them, so they must have standing in this team and be within the caller's
    /// reach, and that is checked for the caller too: a personal token in a team is lent by someone who stands in it — a
    /// member, or an instance Admin, who stands in every team.
    /// </summary>
    private async Task<Guid> ResolveOwnerWithinReachAsync(Guid? intendedOwnerUserId, CancellationToken cancellationToken)
    {
        var ownerUserId = intendedOwnerUserId ?? _currentUser.Id!.Value;

        await _members.EnsureMayActOnAsync(ownerUserId, cancellationToken).ConfigureAwait(false);

        return ownerUserId;
    }

    /// <summary>
    /// The callback can land up to <see cref="StateTtl"/> after Init, and the owner may have left or been removed — or, an
    /// instance Admin outside the team, lost the role — in between. A token minted for them now would be a live credential
    /// of someone without standing in the team — one the team could bind through, and one the repair sweep would hand their
    /// retired bindings to — so the code is not exchanged at all. Runs anonymous, so it asks standing directly
    /// (<see cref="TeamStanding"/>) rather than through the caller's reach.
    /// </summary>
    private async Task EnsureOwnerStillInTeamAsync(OAuthPendingState pending, CancellationToken cancellationToken)
    {
        if (pending.IntendedOwnerUserId is not Guid ownerId) return;

        var teamLive = await _db.Team.AsNoTracking().AnyAsync(t => t.Id == pending.TeamId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (!teamLive || !await TeamStanding.HeldAsync(_db, pending.TeamId, ownerId, cancellationToken).ConfigureAwait(false))
            throw OwnerLeftTheTeam();
    }

    /// <summary>
    /// The check above runs before the provider round-trip, and the owner can leave during it. So the owner's standing is
    /// asked again once the token is back, and a member's held until this callback commits: a departure that committed
    /// during the exchange refuses the token here, and one that starts now waits for the commit and then retires the token
    /// with the rest of theirs. The refused token is not revoked at the provider — revoking an OAuth grant there can end
    /// the person's tokens elsewhere — it is simply never stored.
    /// </summary>
    private async Task HoldOwnerInTeamAsync(OAuthPendingState pending, CancellationToken cancellationToken)
    {
        if (pending.IntendedOwnerUserId is not Guid ownerId) return;

        if (!await _members.HoldMembershipAsync(pending.TeamId, ownerId, cancellationToken).ConfigureAwait(false))
            throw OwnerLeftTheTeam();
    }

    private static OAuthCallbackException OwnerLeftTheTeam() => new("the person this connection was started for no longer has standing in the team");

    private async Task<ProviderInstance> LoadOwnedInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _db.ProviderInstance.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new OAuthCallbackException($"provider instance {id} not found");

        if (instance.TeamId != _currentTeam.Id!.Value) throw new OAuthCallbackException("provider instance does not belong to the current team");

        return instance;
    }

    // Complete-internals (callback runs anonymous, no current-team check).

    /// <summary>
    /// The fast path: consumes the state only together with the nonce of the browser that started the flow. A callback
    /// from any other browser — the person an authorize link was forwarded to, a browser that could not keep the cookie,
    /// or nobody at all — matches no row, so nothing is consumed, exchanged or persisted here.
    /// </summary>
    private async Task<OAuthPendingState?> ConsumeBrowserBoundStateAsync(string state, string? browserNonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(browserNonce)) return null;

        return await _stateStore.ConsumeAsync(state, OAuthBrowserNonce.Hash(browserNonce), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A callback with no binding for its state still consumes nothing: it is handed to the SPA's completion page, where
    /// the signed-in person who started the connect finishes it (<see cref="CompleteAsInitiatorAsync"/>). That page is on
    /// an origin the deployment serves its web app from, never wherever the starter asked — the code rides along, and a
    /// forwarded authorize link must not deliver someone's code to its starter. A state that is not live is refused with
    /// the one message every refusal shares: telling "wrong browser" from "no such state" would only confirm which
    /// states are live.
    /// </summary>
    private async Task<CompleteCredentialOAuthResult> HandOffToInitiatorAsync(string state, CancellationToken cancellationToken)
    {
        var pending = await _stateStore.FindLiveAsync(state, cancellationToken).ConfigureAwait(false) ?? throw UnusableState();

        return new CompleteCredentialOAuthResult { ReturnUrl = OAuthReturnUrl.CompletionPageFor(pending.ReturnUrl, _returnOrigins.Value), BrowserNonceCookiePath = ResolveCallbackUri().AbsolutePath };
    }

    /// <summary>
    /// Consumes the state only for the person who started the connect. Anyone else — the signed-in victim of a forwarded
    /// authorize link — matches no row, so nothing is consumed and the starter can still finish.
    /// </summary>
    private async Task<OAuthPendingState> ConsumeInitiatorBoundStateAsync(string state, CancellationToken cancellationToken) =>
        await _stateStore.ConsumeForInitiatorAsync(state, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false) ?? throw NotStartedByCaller();

    private static OAuthCallbackException UnusableState() => new("state token missing, expired, already consumed, or not started in this browser");

    private static OAuthCallbackException NotStartedByCaller() => new("state token missing, expired, already consumed, or not started by you");

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
