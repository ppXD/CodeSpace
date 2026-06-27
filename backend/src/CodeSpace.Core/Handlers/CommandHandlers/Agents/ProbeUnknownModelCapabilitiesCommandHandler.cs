using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Delegates the backfill to <see cref="IModelCapabilityProbeService"/>.</summary>
public sealed class ProbeUnknownModelCapabilitiesCommandHandler : IRequestHandler<ProbeUnknownModelCapabilitiesCommand, ProbeUnknownModelCapabilitiesResponse>
{
    private readonly IModelCapabilityProbeService _probe;

    public ProbeUnknownModelCapabilitiesCommandHandler(IModelCapabilityProbeService probe) { _probe = probe; }

    public async Task<ProbeUnknownModelCapabilitiesResponse> Handle(ProbeUnknownModelCapabilitiesCommand request, CancellationToken cancellationToken)
    {
        var teamsProbed = await _probe.ProbeAllPendingAsync(cancellationToken).ConfigureAwait(false);

        return new ProbeUnknownModelCapabilitiesResponse { TeamsProbed = teamsProbed };
    }
}
