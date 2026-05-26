using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class UpdateWorkflowCommandHandler : IRequestHandler<UpdateWorkflowCommand, Unit>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public UpdateWorkflowCommandHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<Unit> Handle(UpdateWorkflowCommand request, CancellationToken cancellationToken)
    {
        await _service.UpdateAsync(request.WorkflowId, _currentTeam.Id!.Value, request.Name, request.Description, request.Definition, request.Activations, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
