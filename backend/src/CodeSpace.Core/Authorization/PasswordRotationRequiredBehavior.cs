using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Exceptions;
using MediatR;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// Blocks every MediatR request from a signed-in user whose password is flagged for
/// rotation. Only requests marked <see cref="IBypassPasswordRotationGuard"/> get
/// through — currently just ChangePasswordCommand.
///
/// Unauthenticated requests (sign-in, OAuth callback) pass — there's no rotation flag
/// to honor when there's no user. Background flows (BackgroundSeederUser) also pass
/// because they hardcode <c>PasswordMustChange = false</c>.
/// </summary>
public sealed class PasswordRotationRequiredBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly ICurrentUser _currentUser;

    public PasswordRotationRequiredBehavior(ICurrentUser currentUser) { _currentUser = currentUser; }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is IBypassPasswordRotationGuard) return await next().ConfigureAwait(false);
        if (_currentUser.Id == null) return await next().ConfigureAwait(false);
        if (!_currentUser.PasswordMustChange) return await next().ConfigureAwait(false);

        throw new PasswordRotationRequiredException();
    }
}
