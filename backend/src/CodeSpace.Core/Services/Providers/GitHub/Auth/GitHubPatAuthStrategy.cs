using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitHub.Auth;

public sealed class GitHubPatAuthStrategy : IProviderAuthStrategy
{
    private readonly ICredentialResolver _credentialResolver;

    public GitHubPatAuthStrategy(ICredentialResolver credentialResolver) { _credentialResolver = credentialResolver; }

    public ProviderKind Kind => ProviderKind.GitHub;
    public AuthType AuthType => AuthType.Pat;

    public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var payload = _credentialResolver.Resolve(context.Credential) as PatPayload
            ?? throw new InvalidOperationException($"Credential {context.Credential.Id} has AuthType=Pat but payload is not PatPayload — encrypted blob may be corrupt");

        return Task.FromResult(new ResolvedAuth { Token = payload.Token });
    }
}
