using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// The provider side of ending an OAuth credential, shared by a Disconnect and by deleting the connection the credential
/// was made through. The refresh token goes first — on most providers it is the grant — then the access token. The
/// client secret is the one it is handed rather than one it reads off the connection: a delete reads the secret before
/// it blanks it, and revokes only after its transaction commits, when the connection no longer holds it. Whether the
/// provider is asked to end the user's whole grant or only these tokens is the caller's to say, on every request.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OAuthGrantRevokerTests
{
    private static readonly ProviderInstance Instance = new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = "https://gitlab.example", OauthClientId = "client-1" };

    [Fact]
    public async Task Revokes_the_refresh_token_then_the_access_token_with_the_client_secret_it_is_handed()
    {
        var client = new RecordingOAuthClient();

        await Revoker(client).RevokeAsync(Instance, "client-secret-CANARY", new OAuthPayload { AccessToken = "at-CANARY", RefreshToken = "rt-CANARY" }, wholeGrant: true, CancellationToken.None);

        client.Revokes.Select(r => (r.Token, r.TokenTypeHint)).ShouldBe(new[] { ("rt-CANARY", "refresh_token"), ("at-CANARY", "access_token") });
        client.Revokes.ShouldAllBe(r => r.ClientId == "client-1" && r.ClientSecret == "client-secret-CANARY" && r.Instance == Instance);
    }

    [Fact]
    public async Task Revokes_only_the_access_token_of_a_grant_without_a_refresh_token()
    {
        var client = new RecordingOAuthClient();

        await Revoker(client).RevokeAsync(Instance, "client-secret", new OAuthPayload { AccessToken = "at-only" }, wholeGrant: true, CancellationToken.None);

        client.Revokes.Select(r => r.Token).ShouldBe(new[] { "at-only" });
    }

    [Theory]
    [InlineData(true)]   // a Disconnect: the user's whole grant to the OAuth App
    [InlineData(false)]  // a connection delete: only the tokens this credential held
    public async Task Asks_for_the_scope_it_is_told_on_every_token(bool wholeGrant)
    {
        var client = new RecordingOAuthClient();

        await Revoker(client).RevokeAsync(Instance, "client-secret", new OAuthPayload { AccessToken = "at", RefreshToken = "rt" }, wholeGrant, CancellationToken.None);

        client.Revokes.Count.ShouldBe(2, "fixture check: both tokens were revoked");
        client.Revokes.ShouldAllBe(r => r.WholeGrant == wholeGrant, "the provider client decides what each scope means at its endpoints; the revoker must pass the caller's choice through untouched");
    }

    [Theory]
    [InlineData(true)]   // the stored envelope opens and holds OAuth tokens
    [InlineData(false)]  // it does not open — a rotated key ring, a blanked row
    public void Reads_the_tokens_of_a_readable_OAuth_payload_and_nothing_from_an_unreadable_one(bool readable)
    {
        var serializer = new CredentialPayloadSerializer();
        var envelope = readable ? PrefixEncryptor.Seal(serializer.Serialize(new OAuthPayload { AccessToken = "at-read", RefreshToken = "rt-read" })) : "not-an-envelope";
        var credential = new Credential { Id = Guid.NewGuid(), AuthType = AuthType.OAuth, DisplayName = "oauth", EncryptedPayload = envelope };

        var payload = Revoker(new RecordingOAuthClient()).ReadPayload(credential);

        if (!readable)
        {
            payload.ShouldBeNull();
            return;
        }

        payload.ShouldNotBeNull();
        payload.AccessToken.ShouldBe("at-read");
        payload.RefreshToken.ShouldBe("rt-read");
    }

    private static OAuthGrantRevoker Revoker(RecordingOAuthClient client) => new(new SingleClientRegistry(client), new PrefixEncryptor(), new CredentialPayloadSerializer());

    private sealed class RecordingOAuthClient : IOAuthClient
    {
        public List<OAuthRevokeInput> Revokes { get; } = new();

        public ProviderKind Kind => ProviderKind.GitLab;

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken)
        {
            Revokes.Add(input);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleClientRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;

        public SingleClientRegistry(IOAuthClient client) { _client = client; }

        public IOAuthClient Get(ProviderKind kind) => kind == _client.Kind ? _client : throw new NotSupportedException($"No OAuth client registered for provider {kind}");
    }

    /// <summary>An envelope that opens only when it carries the prefix it was sealed with, as a real key ring opens only its own.</summary>
    private sealed class PrefixEncryptor : IPayloadEncryptor
    {
        private const string Prefix = "sealed:";

        public static string Seal(string plaintext) => Prefix + plaintext;

        public string Encrypt(string plaintext) => Seal(plaintext);

        public string Decrypt(string ciphertext) => ciphertext.StartsWith(Prefix, StringComparison.Ordinal) ? ciphertext[Prefix.Length..] : throw new InvalidOperationException("the envelope does not open");
    }
}
