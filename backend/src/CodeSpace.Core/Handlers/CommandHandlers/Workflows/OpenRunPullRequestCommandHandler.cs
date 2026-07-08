using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Sessions.Room;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class OpenRunPullRequestCommandHandler : IRequestHandler<OpenRunPullRequestCommand, RoomPullRequestResult>
{
    private readonly IRoomPullRequestService _service;
    private readonly ICurrentTeam _currentTeam;

    public OpenRunPullRequestCommandHandler(IRoomPullRequestService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    // No act-as-user yet — mirrors git.open_change_set's own v1 scope (per-user attribution on a fan-out of N
    // opens needs the per-node identity-proof gate the single git.open_pr node uses; a follow-on, not this PR).
    public async Task<RoomPullRequestResult> Handle(OpenRunPullRequestCommand request, CancellationToken cancellationToken) =>
        await _service.OpenAsync(request.WorkflowRunId, _currentTeam.Id!.Value, actorUserId: null, cancellationToken).ConfigureAwait(false);
}
