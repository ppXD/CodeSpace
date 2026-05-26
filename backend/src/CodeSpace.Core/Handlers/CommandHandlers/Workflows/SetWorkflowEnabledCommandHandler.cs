using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class SetWorkflowEnabledCommandHandler : IRequestHandler<SetWorkflowEnabledCommand, Unit>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public SetWorkflowEnabledCommandHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<Unit> Handle(SetWorkflowEnabledCommand request, CancellationToken cancellationToken)
    {
        await _service.SetEnabledAsync(request.WorkflowId, _currentTeam.Id!.Value, request.Enabled, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
