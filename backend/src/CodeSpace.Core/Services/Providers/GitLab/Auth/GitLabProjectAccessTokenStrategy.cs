using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab.Auth;

public sealed class GitLabProjectAccessTokenStrategy : IProviderAuthStrategy
{
    private readonly ICredentialResolver _credentialResolver;

    public GitLabProjectAccessTokenStrategy(ICredentialResolver credentialResolver) { _credentialResolver = credentialResolver; }

    public ProviderKind Kind => ProviderKind.GitLab;
    public AuthType AuthType => AuthType.ProjectAccessToken;

    public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var payload = _credentialResolver.Resolve(context.Credential) as ProjectAccessTokenPayload
            ?? throw new InvalidOperationException($"Credential {context.Credential.Id} has AuthType=ProjectAccessToken but payload is not ProjectAccessTokenPayload — encrypted blob may be corrupt");

        return Task.FromResult(new ResolvedAuth { Token = payload.Token });
    }
}
