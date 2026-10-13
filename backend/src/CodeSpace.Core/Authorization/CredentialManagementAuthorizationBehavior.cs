using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// The owner of a Personal credential manages it on membership alone; every other caller needs
/// <see cref="TeamPermissions.CredentialsManage"/>. Runs inside <see cref="CredentialAccessAuthorizationBehavior{TRequest,TResponse}"/>,
/// which has already proven the credential is the header team's and the caller a member of it.
/// </summary>
public sealed class CredentialManagementAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireCredentialManagement
{
    private readonly CredentialOwnerOrPermission _rule;

    public CredentialManagementAuthorizationBehavior(CredentialOwnerOrPermission rule) { _rule = rule; }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        await _rule.EnsureAsync(request.CredentialId, TeamPermissions.CredentialsManage, cancellationToken).ConfigureAwait(false);

        return await next().ConfigureAwait(false);
    }
}
