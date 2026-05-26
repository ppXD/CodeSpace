using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;

namespace CodeSpace.Core.Authorization;

public sealed class GlobalAdminAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireGlobalAdmin
{
    private readonly ICurrentUser _currentUser;

    public GlobalAdminAuthorizationBehavior(ICurrentUser currentUser) { _currentUser = currentUser; }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasRole(Roles.Admin)) throw new TenantAccessDeniedException(_currentUser.Id, Guid.Empty, $"role '{Roles.Admin}' required");

        return await next().ConfigureAwait(false);
    }
}
