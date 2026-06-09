using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Rule 16 — thin handler. Delegates to <see cref="IAgentRunReconcilerService"/> and maps its summary onto the response DTO.</summary>
public sealed class ReconcileStuckAgentRunsCommandHandler : IRequestHandler<ReconcileStuckAgentRunsCommand, ReconcileStuckAgentRunsResponse>
{
    private readonly IAgentRunReconcilerService _service;

    public ReconcileStuckAgentRunsCommandHandler(IAgentRunReconcilerService service) { _service = service; }

    public async Task<ReconcileStuckAgentRunsResponse> Handle(ReconcileStuckAgentRunsCommand request, CancellationToken cancellationToken)
    {
        var summary = await _service.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        return new ReconcileStuckAgentRunsResponse { MarkedAbandonedFromRunning = summary.MarkedAbandonedFromRunning };
    }
}
