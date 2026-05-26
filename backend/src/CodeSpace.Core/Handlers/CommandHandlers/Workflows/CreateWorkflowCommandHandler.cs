using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class CreateWorkflowCommandHandler : IRequestHandler<CreateWorkflowCommand, Guid>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;

    public CreateWorkflowCommandHandler(IWorkflowService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<Guid> Handle(CreateWorkflowCommand request, CancellationToken cancellationToken) =>
        _service.CreateAsync(_currentTeam.Id!.Value, request.Name, request.Description, request.Definition, request.Activations, request.Enabled, cancellationToken);
}
