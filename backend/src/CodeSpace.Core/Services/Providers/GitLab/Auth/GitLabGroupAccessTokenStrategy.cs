using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab.Auth;

public sealed class GitLabGroupAccessTokenStrategy : IProviderAuthStrategy
{
    private readonly ICredentialResolver _credentialResolver;

    public GitLabGroupAccessTokenStrategy(ICredentialResolver credentialResolver) { _credentialResolver = credentialResolver; }

    public ProviderKind Kind => ProviderKind.GitLab;
    public AuthType AuthType => AuthType.GroupAccessToken;

    public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var payload = _credentialResolver.Resolve(context.Credential) as GroupAccessTokenPayload
            ?? throw new InvalidOperationException($"Credential {context.Credential.Id} has AuthType=GroupAccessToken but payload is not GroupAccessTokenPayload — encrypted blob may be corrupt");

        return Task.FromResult(new ResolvedAuth { Token = payload.Token });
    }
}
