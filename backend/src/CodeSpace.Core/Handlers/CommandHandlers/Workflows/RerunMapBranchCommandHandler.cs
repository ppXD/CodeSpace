using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class RerunMapBranchCommandHandler : IRequestHandler<RerunMapBranchCommand, Guid>
{
    private readonly IWorkflowService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public RerunMapBranchCommandHandler(IWorkflowService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Guid> Handle(RerunMapBranchCommand request, CancellationToken cancellationToken) =>
        await _service.RerunMapBranchAsync(request.OriginalRunId, request.MapNodeId, request.BranchIndex, _currentTeam.Id!.Value, _currentUser.Id!.Value, request.OperationId, cancellationToken).ConfigureAwait(false);
}
