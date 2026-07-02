using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class ReissueWaitCommandHandler : IRequestHandler<ReissueWaitCommand, ReissueWaitOutcome>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ReissueWaitCommandHandler(IWorkflowService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<ReissueWaitOutcome> Handle(ReissueWaitCommand request, CancellationToken cancellationToken) =>
        await _service.ReissueWaitAsync(request.RunId, request.WaitId, _currentTeam.Id!.Value, _currentUser.Id!.Value, request.Body?.GetRawText(), cancellationToken).ConfigureAwait(false);
}
