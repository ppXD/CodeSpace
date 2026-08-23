using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class GetWorkflowRunPendingWaitObservationQueryHandler : IRequestHandler<GetWorkflowRunPendingWaitObservationQuery, WorkflowRunPendingWaitObservation?>
{
    private readonly IWorkflowRunPendingWaitObservationReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunPendingWaitObservationQueryHandler(IWorkflowRunPendingWaitObservationReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunPendingWaitObservation?> Handle(GetWorkflowRunPendingWaitObservationQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
