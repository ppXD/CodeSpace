using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// A connect that names a credential to re-authorize is held to the credential's bar — its owner on membership alone, anyone else
/// <see cref="TeamPermissions.CredentialsManage"/> — and one that names none mints a credential, which is the permission's alone.
/// Runs inside <see cref="TeamMembershipAuthorizationBehavior{TRequest,TResponse}"/>, which has already proven the caller a member of
/// the header team; <see cref="CredentialOwnerOrPermission"/> proves the named credential is that team's.
/// </summary>
public sealed class CredentialReauthorizationAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireCredentialReauthorization
{
    private readonly CredentialOwnerOrPermission _rule;

    public CredentialReauthorizationAuthorizationBehavior(CredentialOwnerOrPermission rule) { _rule = rule; }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        await _rule.EnsureAsync(request.ReplaceCredentialId, TeamPermissions.CredentialsManage, cancellationToken).ConfigureAwait(false);

        return await next().ConfigureAwait(false);
    }
}
