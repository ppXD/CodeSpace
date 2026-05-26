using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Enums;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// Replaces <see cref="GitLabOAuthClient"/> / <see cref="GitHubOAuthClient"/> for full-flow
/// tests. Records every call so the test can assert the credential handler passed through
/// the right code / verifier / redirect_uri. Returns whatever token payload the test sets.
/// </summary>
internal sealed class StubOAuthClient : IOAuthClient
{
    public StubOAuthClient(ProviderKind kind, OAuthTokenResponse exchangeResult)
    {
        Kind = kind;
        ExchangeResult = exchangeResult;
        AuthorizeUrlTemplate = new Uri($"https://oauth.stub/{kind.ToString().ToLowerInvariant()}/authorize");
    }

    public ProviderKind Kind { get; }
    public OAuthTokenResponse ExchangeResult { get; set; }
    public OAuthTokenResponse? RefreshResult { get; set; }
    public Uri AuthorizeUrlTemplate { get; set; }

    public OAuthAuthorizeInput? LastAuthorize { get; private set; }
    public OAuthCodeExchangeInput? LastExchange { get; private set; }
    public OAuthRefreshInput? LastRefresh { get; private set; }

    public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input)
    {
        LastAuthorize = input;
        return new Uri($"{AuthorizeUrlTemplate}?client_id={input.ClientId}&state={input.State}");
    }

    public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken)
    {
        LastExchange = input;
        return Task.FromResult(ExchangeResult);
    }

    public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken)
    {
        LastRefresh = input;
        return Task.FromResult(RefreshResult ?? ExchangeResult);
    }

    public OAuthRevokeInput? LastRevoke { get; set; }
    public bool RevokeShouldThrow { get; set; }

    public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken)
    {
        LastRevoke = input;
        if (RevokeShouldThrow) throw new InvalidOperationException("stub: revoke failed");
        return Task.CompletedTask;
    }
}
