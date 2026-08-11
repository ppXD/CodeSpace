using CodeSpace.Core.Services.Auth;
using CodeSpace.Messages.Commands.Auth;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Auth;

/// <summary>Rule 16 — thin handler. The roster query and the per-user warning live in <see cref="IUnrotatedBootstrapPasswordAudit"/>.</summary>
public sealed class WarnUnrotatedBootstrapPasswordsCommandHandler : IRequestHandler<WarnUnrotatedBootstrapPasswordsCommand, WarnUnrotatedBootstrapPasswordsResponse>
{
    private readonly IUnrotatedBootstrapPasswordAudit _audit;

    public WarnUnrotatedBootstrapPasswordsCommandHandler(IUnrotatedBootstrapPasswordAudit audit) { _audit = audit; }

    public async Task<WarnUnrotatedBootstrapPasswordsResponse> Handle(WarnUnrotatedBootstrapPasswordsCommand request, CancellationToken cancellationToken)
    {
        var unrotated = await _audit.WarnUnrotatedAsync(cancellationToken).ConfigureAwait(false);

        return new WarnUnrotatedBootstrapPasswordsResponse { Unrotated = unrotated };
    }
}
