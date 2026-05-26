using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class DeleteWorkflowCommandHandler : IRequestHandler<DeleteWorkflowCommand, Unit>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public DeleteWorkflowCommandHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<Unit> Handle(DeleteWorkflowCommand request, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(request.WorkflowId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
