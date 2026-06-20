using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Queries.Decisions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Decisions;

/// <summary>Thin dispatcher (Rule 16): resolves the caller's team and asks the queue service. All cross-grain query logic lives in <see cref="IDecisionQueueService"/>.</summary>
public sealed class ListPendingDecisionsQueryHandler : IRequestHandler<ListPendingDecisionsQuery, IReadOnlyList<PendingDecision>>
{
    private readonly IDecisionQueueService _queue;
    private readonly ICurrentTeam _currentTeam;

    public ListPendingDecisionsQueryHandler(IDecisionQueueService queue, ICurrentTeam currentTeam)
    {
        _queue = queue;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<PendingDecision>> Handle(ListPendingDecisionsQuery request, CancellationToken cancellationToken) =>
        await _queue.ListPendingAsync(_currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
