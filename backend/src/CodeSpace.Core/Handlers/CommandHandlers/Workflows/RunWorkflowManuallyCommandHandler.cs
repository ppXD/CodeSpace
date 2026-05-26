using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class RunWorkflowManuallyCommandHandler : IRequestHandler<RunWorkflowManuallyCommand, Guid>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public RunWorkflowManuallyCommandHandler(IWorkflowService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<Guid> Handle(RunWorkflowManuallyCommand request, CancellationToken cancellationToken) =>
        _service.RunManuallyAsync(request.WorkflowId, _currentTeam.Id!.Value, _currentUser.Id!.Value, request.Payload, cancellationToken);
}
