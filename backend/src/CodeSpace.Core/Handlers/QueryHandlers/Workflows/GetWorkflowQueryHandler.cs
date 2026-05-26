using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class GetWorkflowQueryHandler : IRequestHandler<GetWorkflowQuery, WorkflowDetail?>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowQueryHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<WorkflowDetail?> Handle(GetWorkflowQuery request, CancellationToken cancellationToken) =>
        _service.GetAsync(request.WorkflowId, _currentTeam.Id!.Value, cancellationToken);
}
