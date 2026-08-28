using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class ProbeStaleStorageDestinationsCommandHandler : IRequestHandler<ProbeStaleStorageDestinationsCommand, ProbeStaleStorageDestinationsResponse>
{
    private readonly IStorageDestinationHealthSweep _sweep;

    public ProbeStaleStorageDestinationsCommandHandler(IStorageDestinationHealthSweep sweep) { _sweep = sweep; }

    public async Task<ProbeStaleStorageDestinationsResponse> Handle(ProbeStaleStorageDestinationsCommand request, CancellationToken cancellationToken) =>
        new() { DestinationsProbed = await _sweep.ProbeStaleAsync(cancellationToken).ConfigureAwait(false) };
}
