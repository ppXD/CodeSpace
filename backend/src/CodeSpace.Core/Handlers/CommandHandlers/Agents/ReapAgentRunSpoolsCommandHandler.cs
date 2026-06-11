using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Delegates the spool reclamation to <see cref="IAgentRunSpoolReaper"/>.</summary>
public sealed class ReapAgentRunSpoolsCommandHandler : IRequestHandler<ReapAgentRunSpoolsCommand, ReapAgentRunSpoolsResponse>
{
    private readonly IAgentRunSpoolReaper _reaper;

    public ReapAgentRunSpoolsCommandHandler(IAgentRunSpoolReaper reaper) { _reaper = reaper; }

    public async Task<ReapAgentRunSpoolsResponse> Handle(ReapAgentRunSpoolsCommand request, CancellationToken cancellationToken)
    {
        var reaped = await _reaper.ReapAsync(cancellationToken).ConfigureAwait(false);

        return new ReapAgentRunSpoolsResponse { Reaped = reaped };
    }
}
