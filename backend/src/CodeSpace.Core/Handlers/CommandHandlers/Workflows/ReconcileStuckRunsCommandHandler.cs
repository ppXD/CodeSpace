using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>
/// Rule 16 — thin handler. Delegates to <see cref="IStuckRunReconcilerService"/> and maps
/// the service's domain summary onto the messages-layer response DTO.
/// </summary>
public sealed class ReconcileStuckRunsCommandHandler : IRequestHandler<ReconcileStuckRunsCommand, ReconcileStuckRunsResponse>
{
    private readonly IStuckRunReconcilerService _service;

    public ReconcileStuckRunsCommandHandler(IStuckRunReconcilerService service) { _service = service; }

    public async Task<ReconcileStuckRunsResponse> Handle(ReconcileStuckRunsCommand request, CancellationToken cancellationToken)
    {
        var summary = await _service.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return new ReconcileStuckRunsResponse
        {
            RedispatchedFromPending = summary.RedispatchedFromPending,
            RevertedFromEnqueued = summary.RevertedFromEnqueued,
            MarkedAbandonedFromRunning = summary.MarkedAbandonedFromRunning,
        };
    }
}
