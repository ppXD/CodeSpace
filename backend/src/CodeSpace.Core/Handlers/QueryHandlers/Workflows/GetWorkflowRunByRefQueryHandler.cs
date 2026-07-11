using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class GetWorkflowRunByRefQueryHandler : IRequestHandler<GetWorkflowRunByRefQuery, WorkflowRunDetail?>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunByRefQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<WorkflowRunDetail?> Handle(GetWorkflowRunByRefQuery request, CancellationToken cancellationToken) =>
        _service.GetRunByRefAsync(request.IdOrNumber, _currentTeam.Id!.Value, cancellationToken);
}
