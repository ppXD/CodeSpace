using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitHub.Auth;

public sealed class GitHubOAuthAuthStrategy : IProviderAuthStrategy
{
    private readonly ICredentialResolver _credentialResolver;
    private readonly IOAuthTokenRefresher _refresher;

    public GitHubOAuthAuthStrategy(ICredentialResolver credentialResolver, IOAuthTokenRefresher refresher)
    {
        _credentialResolver = credentialResolver;
        _refresher = refresher;
    }

    public ProviderKind Kind => ProviderKind.GitHub;
    public AuthType AuthType => AuthType.OAuth;

    public async Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var payload = _credentialResolver.Resolve(context.Credential) as OAuthPayload
            ?? throw new InvalidOperationException($"Credential {context.Credential.Id} has AuthType=OAuth but payload is not OAuthPayload — encrypted blob may be corrupt");

        var effective = await _refresher.RefreshIfNeededAsync(context, payload, cancellationToken).ConfigureAwait(false);

        return new ResolvedAuth { Token = effective.AccessToken, ExpiresAt = effective.ExpiresAt };
    }
}
