using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="ICredentialSuccessionService.RepairStrandedAsync"/>.</summary>
public sealed class RepairDisconnectedCredentialBindingsCommandHandler : IRequestHandler<RepairDisconnectedCredentialBindingsCommand, int>
{
    private readonly ICredentialSuccessionService _succession;

    public RepairDisconnectedCredentialBindingsCommandHandler(ICredentialSuccessionService succession)
    {
        _succession = succession;
    }

    public async Task<int> Handle(RepairDisconnectedCredentialBindingsCommand request, CancellationToken cancellationToken)
    {
        return await _succession.RepairStrandedAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
    }
}
