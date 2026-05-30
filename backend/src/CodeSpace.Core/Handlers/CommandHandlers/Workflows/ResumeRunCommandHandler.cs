using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class ResumeRunCommandHandler : IRequestHandler<ResumeRunCommand, bool>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ResumeRunCommandHandler(IWorkflowService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<bool> Handle(ResumeRunCommand request, CancellationToken cancellationToken) =>
        await _service.ApproveRunAsync(request.RunId, _currentTeam.Id!.Value, _currentUser.Id!.Value, request.Approved, request.Comment, cancellationToken).ConfigureAwait(false);
}
