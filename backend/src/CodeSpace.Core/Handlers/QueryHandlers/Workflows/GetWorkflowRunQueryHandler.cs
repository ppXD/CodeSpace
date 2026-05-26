using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class GetWorkflowRunQueryHandler : IRequestHandler<GetWorkflowRunQuery, WorkflowRunDetail?>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<WorkflowRunDetail?> Handle(GetWorkflowRunQuery request, CancellationToken cancellationToken) =>
        _service.GetRunAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken);
}
