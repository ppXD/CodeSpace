using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Delegates the orphan settlement to <see cref="IAgentRunOrphanReaper"/>.</summary>
public sealed class ReapAgentRunOrphansCommandHandler : IRequestHandler<ReapAgentRunOrphansCommand, ReapAgentRunOrphansResponse>
{
    private readonly IAgentRunOrphanReaper _reaper;

    public ReapAgentRunOrphansCommandHandler(IAgentRunOrphanReaper reaper) { _reaper = reaper; }

    public async Task<ReapAgentRunOrphansResponse> Handle(ReapAgentRunOrphansCommand request, CancellationToken cancellationToken)
    {
        var compensated = await _reaper.ReapAsync(cancellationToken).ConfigureAwait(false);

        return new ReapAgentRunOrphansResponse { Compensated = compensated };
    }
}
