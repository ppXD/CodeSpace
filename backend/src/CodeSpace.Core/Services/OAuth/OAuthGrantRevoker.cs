using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Scoped, not singleton, so a scope that swaps the OAuth client registry (a test standing in for the provider) is the
/// registry this revoker asks.
/// </summary>
public sealed class OAuthGrantRevoker : IOAuthGrantRevoker, IScopedDependency
{
    private readonly IOAuthClientRegistry _oauthClients;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;

    public OAuthGrantRevoker(IOAuthClientRegistry oauthClients, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer)
    {
        _oauthClients = oauthClients;
        _encryptor = encryptor;
        _serializer = serializer;
    }

    public OAuthPayload? ReadPayload(Credential credential)
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

    public async Task RevokeAsync(ProviderInstance instance, string clientSecret, OAuthPayload payload, bool wholeGrant, CancellationToken cancellationToken)
    {
        var client = _oauthClients.Get(instance.Provider);

        // Revoke refresh_token first (more durable — kills the grant on most providers), then
        // access_token. Both are idempotent on the provider side; the caller's catch clears
        // local payload regardless.
        if (!string.IsNullOrEmpty(payload.RefreshToken))
            await client.RevokeAsync(Input(instance, clientSecret, payload.RefreshToken, "refresh_token", wholeGrant), cancellationToken).ConfigureAwait(false);

        await client.RevokeAsync(Input(instance, clientSecret, payload.AccessToken, "access_token", wholeGrant), cancellationToken).ConfigureAwait(false);
    }

    private static OAuthRevokeInput Input(ProviderInstance instance, string clientSecret, string token, string tokenTypeHint, bool wholeGrant) => new()
    {
        Instance = instance,
        ClientId = instance.OauthClientId!,
        ClientSecret = clientSecret,
        Token = token,
        TokenTypeHint = tokenTypeHint,
        WholeGrant = wholeGrant
    };
}
