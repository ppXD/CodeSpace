using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
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
    private readonly OAuthCallbackUrlSetting _callbackUrlSetting;

    public OAuthFlowService(CodeSpaceDbContext db, IOAuthClientRegistry oauthClients, IOAuthStateStore stateStore, IPkceGenerator pkce, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, ICurrentTeam currentTeam, ICurrentUser currentUser, OAuthCallbackUrlSetting callbackUrlSetting)
    {
        _db = db;
        _oauthClients = oauthClients;
        _stateStore = stateStore;
        _pkce = pkce;
        _encryptor = encryptor;
        _serializer = serializer;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _callbackUrlSetting = callbackUrlSetting;
    }

    public async Task<InitCredentialOAuthResult> InitAsync(Guid providerInstanceId, string displayName, Guid? intendedOwnerUserId, string? returnUrl, IReadOnlyList<string>? scopes, CancellationToken cancellationToken)
    {
        var instance = await LoadOwnedInstanceAsync(providerInstanceId, cancellationToken).ConfigureAwait(false);
        var (clientId, _) = ReadOAuthClientCredentials(instance);

        var pkce = _pkce.Generate();

        var pending = await _stateStore.CreateAsync(new OAuthPendingStateInput
        {
            ProviderInstanceId = instance.Id,
            TeamId = _currentTeam.Id!.Value,
            InitiatorUserId = _currentUser.Id!.Value,
            CodeVerifier = pkce.Verifier,
            IntendedDisplayName = displayName,
            IntendedOwnerUserId = intendedOwnerUserId ?? _currentUser.Id!.Value,
            ReturnUrl = returnUrl,
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

        return new InitCredentialOAuthResult { AuthorizeUrl = authorizeUrl, State = pending.State };
    }

    public async Task<CompleteCredentialOAuthResult> CompleteAsync(string state, string code, CancellationToken cancellationToken)
    {
        var pending = await ConsumeStateAsync(state, cancellationToken).ConfigureAwait(false);
        var instance = await LoadInstanceAsync(pending.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
        var (clientId, clientSecret) = ReadOAuthClientCredentials(instance);

        var token = await ExchangeCodeAsync(instance, clientId, clientSecret, code, pending.CodeVerifier, cancellationToken).ConfigureAwait(false);
        var credentialId = PersistCredential(pending, instance, token);

        return new CompleteCredentialOAuthResult
        {
            CredentialId = credentialId,
            TeamId = pending.TeamId,
            ReturnUrl = pending.ReturnUrl ?? "/"
        };
    }

    private async Task<ProviderInstance> LoadOwnedInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _db.ProviderInstance.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new OAuthCallbackException($"provider instance {id} not found");

        if (instance.TeamId != _currentTeam.Id!.Value) throw new OAuthCallbackException("provider instance does not belong to the current team");

        return instance;
    }

    // Complete-internals (callback runs anonymous, no current-team check).

    private async Task<OAuthPendingState> ConsumeStateAsync(string state, CancellationToken cancellationToken)
    {
        var row = await _stateStore.ConsumeAsync(state, cancellationToken).ConfigureAwait(false);

        if (row == null) throw new OAuthCallbackException("state token missing, expired, or already consumed");

        return row;
    }

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

    private Guid PersistCredential(OAuthPendingState state, ProviderInstance instance, OAuthTokenResponse token)
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
        return credential.Id;
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
