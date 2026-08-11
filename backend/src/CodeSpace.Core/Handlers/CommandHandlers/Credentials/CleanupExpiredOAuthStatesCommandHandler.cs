using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Commands.OAuth;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

/// <summary>Rule 16 — thin handler. The expiry predicate and the delete live in <see cref="IOAuthStateCleanup"/>.</summary>
public sealed class CleanupExpiredOAuthStatesCommandHandler : IRequestHandler<CleanupExpiredOAuthStatesCommand, CleanupExpiredOAuthStatesResponse>
{
    private readonly IOAuthStateCleanup _cleanup;

    public CleanupExpiredOAuthStatesCommandHandler(IOAuthStateCleanup cleanup) { _cleanup = cleanup; }

    public async Task<CleanupExpiredOAuthStatesResponse> Handle(CleanupExpiredOAuthStatesCommand request, CancellationToken cancellationToken)
    {
        var deleted = await _cleanup.DeleteExpiredAsync(cancellationToken).ConfigureAwait(false);

        return new CleanupExpiredOAuthStatesResponse { Deleted = deleted };
    }
}
