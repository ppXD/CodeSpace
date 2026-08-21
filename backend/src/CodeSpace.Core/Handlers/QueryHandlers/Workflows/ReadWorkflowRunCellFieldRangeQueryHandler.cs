using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ReadWorkflowRunCellFieldRangeQueryHandler : IRequestHandler<ReadWorkflowRunCellFieldRangeQuery, WorkflowRunCellFieldRangePage?>
{
    private readonly IWorkflowRunCellFieldRangeReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public ReadWorkflowRunCellFieldRangeQueryHandler(IWorkflowRunCellFieldRangeReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunCellFieldRangePage?> Handle(ReadWorkflowRunCellFieldRangeQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(new WorkflowRunCellFieldRangeReadRequest
        {
            TeamId = _currentTeam.Id!.Value,
            RequestedRunId = request.RunId,
            Scope = request.Scope,
            SourceRunId = request.SourceRunId,
            NodeId = request.NodeId,
            IterationKey = request.IterationKey ?? string.Empty,
            Records = new WorkflowRunCellRecordIdentity(request.StateRecordId, request.StateRecordSequence,
                request.FirstStartedRecordId, request.FirstStartedRecordSequence),
            Section = request.Section,
            Name = request.Name,
            Cursor = request.Cursor,
            LimitBytes = request.LimitBytes,
        }, cancellationToken).ConfigureAwait(false);
}
