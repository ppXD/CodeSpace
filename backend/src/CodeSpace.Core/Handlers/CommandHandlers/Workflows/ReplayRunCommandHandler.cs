using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class ReplayRunCommandHandler : IRequestHandler<ReplayRunCommand, Guid>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ReplayRunCommandHandler(IWorkflowService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Guid> Handle(ReplayRunCommand request, CancellationToken cancellationToken) =>
        await _service.ReplayRunAsync(request.OriginalRunId, _currentTeam.Id!.Value, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
}
