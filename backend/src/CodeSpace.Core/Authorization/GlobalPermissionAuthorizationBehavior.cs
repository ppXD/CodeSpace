using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;

namespace CodeSpace.Core.Authorization;

public sealed class GlobalPermissionAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireGlobalPermission
{
    private readonly ICurrentUser _currentUser;

    public GlobalPermissionAuthorizationBehavior(ICurrentUser currentUser) { _currentUser = currentUser; }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Admin holds every instance capability by definition — the role exists to be the account that
        // can grant the others, so making it prove each one separately would only invite a deployment
        // where nobody can grant anything.
        if (_currentUser.HasRole(Roles.Admin)) return await next().ConfigureAwait(false);

        if (!_currentUser.HasPermission(request.RequiredGlobalPermission)) throw new TenantAccessDeniedException(_currentUser.Id, Guid.Empty, $"permission '{request.RequiredGlobalPermission}' required");

        return await next().ConfigureAwait(false);
    }
}
