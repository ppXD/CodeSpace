using System.Net;
using System.Text;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// What each provider's client sends when CodeSpace ends a token. GitHub revokes at two endpoints: the grant delete ends
/// every token GitHub issued the user under the OAuth App — the ones another team connected through the same app holds
/// too — so it is sent only for a whole-grant revoke (a Disconnect); otherwise the token delete ends the one token.
/// GitLab revokes per token (RFC 7009) and sends the same request either way. The URL, method, body and client
/// authentication are asserted as they leave the client, through a recording <see cref="HttpMessageHandler"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OAuthClientRevokeTests
{
    private static readonly ProviderInstance GitHubInstance = new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitHub, DisplayName = "github", BaseUrl = "https://github.com", OauthClientId = "shared-app" };

    private static readonly ProviderInstance GitLabInstance = new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = "https://gitlab.example", OauthClientId = "shared-app" };

    [Fact]
    public void A_revoke_ends_the_whole_grant_unless_told_otherwise()
    {
        var input = new OAuthRevokeInput { Instance = GitHubInstance, ClientId = "shared-app", ClientSecret = "app-secret", Token = "tok" };

        input.WholeGrant.ShouldBeTrue("a Disconnect, and every caller that does not say, keeps the grant-level revoke it always sent");
    }

    [Theory]
    [InlineData(true, "access_token", "/applications/shared-app/grant")]    // a Disconnect: the user's whole grant to the app
    [InlineData(true, "refresh_token", "/applications/shared-app/grant")]   // unchanged: a Disconnect sends both tokens to the grant delete
    [InlineData(false, "access_token", "/applications/shared-app/token")]   // a connection delete: this access token only
    public async Task GitHub_deletes_the_grant_only_for_a_whole_grant_revoke_and_otherwise_the_one_token(bool wholeGrant, string tokenTypeHint, string expectedPath)
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);

        await GitHub(handler).RevokeAsync(Input(GitHubInstance, wholeGrant, "tok-CANARY", tokenTypeHint), CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Delete);
        request.Uri.ShouldBe(new Uri("https://api.github.com" + expectedPath));
        request.Body.ShouldBe("{\"access_token\":\"tok-CANARY\"}");
        request.Authorization.ShouldBe("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("shared-app:app-secret")), "both endpoints authenticate as the OAuth App");
    }

    [Fact]
    public async Task GitHub_sends_nothing_for_a_refresh_token_outside_a_whole_grant_revoke()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);

        await GitHub(handler).RevokeAsync(Input(GitHubInstance, wholeGrant: false, "rt-CANARY", "refresh_token"), CancellationToken.None);

        handler.Requests.ShouldBeEmpty("GitHub has no endpoint that ends a refresh token on its own; the only one that does is the grant delete, which would end the user's tokens in every other team on the same OAuth App");
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]            // revoked
    [InlineData(HttpStatusCode.NotFound)]             // already gone
    [InlineData(HttpStatusCode.UnprocessableContent)] // not a token GitHub knows
    public async Task GitHub_treats_a_token_revoke_that_finds_nothing_left_to_end_as_done(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status);

        await Should.NotThrowAsync(() => GitHub(handler).RevokeAsync(Input(GitHubInstance, wholeGrant: false, "tok", "access_token"), CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_surfaces_a_token_revoke_the_provider_failed()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);

        var failure = await Should.ThrowAsync<OAuthExchangeException>(() => GitHub(handler).RevokeAsync(Input(GitHubInstance, wholeGrant: false, "tok", "access_token"), CancellationToken.None));

        failure.Error.ShouldBe("http_500", "the caller logs a failed revoke as best effort; it must not read as done");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitLab_revokes_the_one_token_whether_or_not_the_whole_grant_is_asked_for(bool wholeGrant)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);

        await GitLab(handler).RevokeAsync(Input(GitLabInstance, wholeGrant, "rt-CANARY", "refresh_token"), CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://gitlab.example/oauth/revoke"));
        request.Body.ShouldBe("client_id=shared-app&client_secret=app-secret&token=rt-CANARY&token_type_hint=refresh_token", "RFC 7009 ends exactly the token presented");
    }

    private static OAuthRevokeInput Input(ProviderInstance instance, bool wholeGrant, string token, string tokenTypeHint) => new()
    {
        Instance = instance,
        ClientId = "shared-app",
        ClientSecret = "app-secret",
        Token = token,
        TokenTypeHint = tokenTypeHint,
        WholeGrant = wholeGrant
    };

    private static GitHubOAuthClient GitHub(RecordingHandler handler) => new(new SingleClientFactory(handler), TimeProvider.System);

    private static GitLabOAuthClient GitLab(RecordingHandler handler) => new(new SingleClientFactory(handler), TimeProvider.System);

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string? Authorization);

    /// <summary>Answers every request with one status and records it whole, as the provider would receive it.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public RecordingHandler(HttpStatusCode status) { _status = status; }

        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString()));

            return new HttpResponseMessage(_status) { Content = new StringContent("{}") };
        }
    }

    /// <summary>Hands out clients over the recording handler; the handler outlives each client the provider client disposes.</summary>
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleClientFactory(HttpMessageHandler handler) { _handler = handler; }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
