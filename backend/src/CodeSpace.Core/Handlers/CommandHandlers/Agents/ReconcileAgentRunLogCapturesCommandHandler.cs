using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class ReconcileAgentRunLogCapturesCommandHandler : IRequestHandler<ReconcileAgentRunLogCapturesCommand, ReconcileAgentRunLogCapturesResponse>
{
    private readonly IAgentRunLogCaptureRecoveryService _recovery;

    public ReconcileAgentRunLogCapturesCommandHandler(IAgentRunLogCaptureRecoveryService recovery) => _recovery = recovery;

    public async Task<ReconcileAgentRunLogCapturesResponse> Handle(ReconcileAgentRunLogCapturesCommand request, CancellationToken cancellationToken)
    {
        var summary = await _recovery.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return new ReconcileAgentRunLogCapturesResponse(summary.Claimed, summary.Completed, summary.CaptureFailed, summary.Superseded, summary.Retried, summary.LostLease)
        {
            ExternalStateIndeterminate = summary.ExternalStateIndeterminate,
        };
    }
}
