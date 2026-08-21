using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.ToolCalls;
using CodeSpace.Messages.Dtos.Workflows.ToolCalls;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListWorkflowRunToolCallsQueryHandler : IRequestHandler<ListWorkflowRunToolCallsQuery, WorkflowRunToolCallPage?>
{
    private readonly IWorkflowRunToolCallReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public ListWorkflowRunToolCallsQueryHandler(IWorkflowRunToolCallReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunToolCallPage?> Handle(ListWorkflowRunToolCallsQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadPageAsync(new WorkflowRunToolCallPageRequest(_currentTeam.Id!.Value, request.RunId, request.Cursor, request.Limit), cancellationToken).ConfigureAwait(false);
}

public sealed class GetWorkflowRunToolCallQueryHandler : IRequestHandler<GetWorkflowRunToolCallQuery, WorkflowRunToolCallDetail?>
{
    private readonly IWorkflowRunToolCallReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunToolCallQueryHandler(IWorkflowRunToolCallReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunToolCallDetail?> Handle(GetWorkflowRunToolCallQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadDetailAsync(new WorkflowRunToolCallDetailRequest(_currentTeam.Id!.Value, request.RunId, request.ToolCallId), cancellationToken).ConfigureAwait(false);
}
