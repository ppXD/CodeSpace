using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab.Auth;

public sealed class GitLabOAuthAuthStrategy : IRecoverableProviderAuthStrategy
{
    private readonly ICredentialResolver _credentialResolver;
    private readonly IOAuthTokenRefresher _refresher;
    private readonly IOAuthTokenRecovery _recovery;

    public GitLabOAuthAuthStrategy(ICredentialResolver credentialResolver, IOAuthTokenRefresher refresher, IOAuthTokenRecovery recovery)
    {
        _credentialResolver = credentialResolver;
        _refresher = refresher;
        _recovery = recovery;
    }

    public ProviderKind Kind => ProviderKind.GitLab;
    public AuthType AuthType => AuthType.OAuth;

    public async Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        await _recovery.StoreKeptPairAsync(context, cancellationToken).ConfigureAwait(false);

        var payload = _credentialResolver.Resolve(context.Credential) as OAuthPayload
            ?? throw new InvalidOperationException($"Credential {context.Credential.Id} has AuthType=OAuth but payload is not OAuthPayload — encrypted blob may be corrupt");

        var effective = await _refresher.RefreshIfNeededAsync(context, payload, cancellationToken).ConfigureAwait(false);

        return new ResolvedAuth { Token = effective.AccessToken, ExpiresAt = effective.ExpiresAt };
    }

    public async Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken)
    {
        var recovered = await _recovery.RecoverAfterRejectionAsync(context, rejectedToken, cancellationToken).ConfigureAwait(false);

        return recovered is null ? null : new ResolvedAuth { Token = recovered.AccessToken, ExpiresAt = recovered.ExpiresAt };
    }
}
