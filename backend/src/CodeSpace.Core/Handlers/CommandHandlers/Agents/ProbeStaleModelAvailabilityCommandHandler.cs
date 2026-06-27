using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Delegates the backfill to <see cref="IModelAvailabilityProbeService"/>.</summary>
public sealed class ProbeStaleModelAvailabilityCommandHandler : IRequestHandler<ProbeStaleModelAvailabilityCommand, ProbeStaleModelAvailabilityResponse>
{
    private readonly IModelAvailabilityProbeService _availability;

    public ProbeStaleModelAvailabilityCommandHandler(IModelAvailabilityProbeService availability) { _availability = availability; }

    public async Task<ProbeStaleModelAvailabilityResponse> Handle(ProbeStaleModelAvailabilityCommand request, CancellationToken cancellationToken)
    {
        var teamsProbed = await _availability.ProbeAllPendingAsync(cancellationToken).ConfigureAwait(false);

        return new ProbeStaleModelAvailabilityResponse { TeamsProbed = teamsProbed };
    }
}
