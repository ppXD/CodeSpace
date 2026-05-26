using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Core.Authorization;

public sealed class AuthenticatedUserAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireAuthenticatedUser
{
    private readonly ICurrentUser _currentUser;

    public AuthenticatedUserAuthorizationBehavior(ICurrentUser currentUser) { _currentUser = currentUser; }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_currentUser.Id == null) throw new UnauthorizedAccessException("authentication required");

        return await next().ConfigureAwait(false);
    }
}
