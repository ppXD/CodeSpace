using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class GetWorkflowRunCellFieldsQueryHandler : IRequestHandler<GetWorkflowRunCellFieldsQuery, WorkflowRunCellFieldPage?>
{
    private readonly IWorkflowRunCellFieldReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunCellFieldsQueryHandler(IWorkflowRunCellFieldReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunCellFieldPage?> Handle(GetWorkflowRunCellFieldsQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(new WorkflowRunCellFieldReadRequest
        {
            TeamId = _currentTeam.Id!.Value,
            RequestedRunId = request.RunId,
            Scope = request.Scope,
            SourceRunId = request.SourceRunId,
            NodeId = request.NodeId,
            IterationKey = request.IterationKey ?? string.Empty,
            Cursor = request.Cursor,
            Limit = request.Limit,
        }, cancellationToken).ConfigureAwait(false);
}
