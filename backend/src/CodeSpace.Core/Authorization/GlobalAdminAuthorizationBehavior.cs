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
        if (!_currentUser.HasRole(Roles.Admin)) throw TenantAccessDeniedException.GlobalAdminRequired(_currentUser.Id, Roles.Admin);

        return await next().ConfigureAwait(false);
    }
}
