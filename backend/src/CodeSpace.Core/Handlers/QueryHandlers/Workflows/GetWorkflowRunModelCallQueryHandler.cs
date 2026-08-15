using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.Messages.Dtos.Workflows.ModelCalls;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class GetWorkflowRunModelCallByIdQueryHandler : IRequestHandler<GetWorkflowRunModelCallByIdQuery, WorkflowRunModelCallDetailMetadata?>
{
    private readonly IWorkflowRunModelCallReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunModelCallByIdQueryHandler(IWorkflowRunModelCallReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunModelCallDetailMetadata?> Handle(GetWorkflowRunModelCallByIdQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadByIdAsync(request.RunId, request.WorkflowRunModelCallId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class GetWorkflowRunModelCallBodyQueryHandler : IRequestHandler<GetWorkflowRunModelCallBodyQuery, WorkflowRunModelCallBodyPage?>
{
    private readonly IWorkflowRunModelCallReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunModelCallBodyQueryHandler(IWorkflowRunModelCallReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunModelCallBodyPage?> Handle(GetWorkflowRunModelCallBodyQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(request.RunId, request.WorkflowRunModelCallId, _currentTeam.Id!.Value, request.Body)
        {
            AttemptId = request.AttemptId,
            OffsetBytes = request.OffsetBytes,
            LimitBytes = request.LimitBytes,
        }, cancellationToken).ConfigureAwait(false);
}

public sealed class GetWorkflowRunModelCallQueryHandler : IRequestHandler<GetWorkflowRunModelCallQuery, WorkflowRunModelCallMetadata?>
{
    private readonly IWorkflowRunModelCallReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunModelCallQueryHandler(IWorkflowRunModelCallReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunModelCallMetadata?> Handle(GetWorkflowRunModelCallQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadMetadataAsync(request.RunId, request.Sequence, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class GetWorkflowRunModelCallPartQueryHandler : IRequestHandler<GetWorkflowRunModelCallPartQuery, WorkflowRunModelCallPartPage?>
{
    private readonly IWorkflowRunModelCallReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunModelCallPartQueryHandler(IWorkflowRunModelCallReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunModelCallPartPage?> Handle(GetWorkflowRunModelCallPartQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadPartAsync(new WorkflowRunModelCallPartReadRequest(request.RunId, request.Sequence, _currentTeam.Id!.Value, request.Part)
        {
            OffsetBytes = request.OffsetBytes,
            LimitBytes = request.LimitBytes,
        }, cancellationToken).ConfigureAwait(false);
}
