using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>Thin dispatcher (Rule 16) — scopes to the CALLER'S team + user and answers the run's pending supervisor ask via <see cref="ISupervisorAskAnswerService"/>. Null (no pending ask / foreign run) → the controller 404-conflates.</summary>
public sealed class AnswerRunAskCommandHandler : IRequestHandler<AnswerRunAskCommand, SupervisorAskAnswerOutcome?>
{
    private readonly ISupervisorAskAnswerService _asks;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AnswerRunAskCommandHandler(ISupervisorAskAnswerService asks, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _asks = asks;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<SupervisorAskAnswerOutcome?> Handle(AnswerRunAskCommand request, CancellationToken cancellationToken) =>
        await _asks.AnswerAsync(request.RunId, _currentTeam.Id!.Value, _currentUser.Id!.Value, request.Answer, cancellationToken).ConfigureAwait(false);
}
