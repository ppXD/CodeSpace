using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Delegates the backfill to <see cref="IModelCapabilityTieringService"/>.</summary>
public sealed class TierStaleModelCapabilitiesCommandHandler : IRequestHandler<TierStaleModelCapabilitiesCommand, TierStaleModelCapabilitiesResponse>
{
    private readonly IModelCapabilityTieringService _tiering;

    public TierStaleModelCapabilitiesCommandHandler(IModelCapabilityTieringService tiering) { _tiering = tiering; }

    public async Task<TierStaleModelCapabilitiesResponse> Handle(TierStaleModelCapabilitiesCommand request, CancellationToken cancellationToken)
    {
        var teamsTiered = await _tiering.TierAllPendingAsync(cancellationToken).ConfigureAwait(false);

        return new TierStaleModelCapabilitiesResponse { TeamsTiered = teamsTiered };
    }
}
