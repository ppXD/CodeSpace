using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Plans;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>Thin dispatcher (Rule 16) — scopes to the CALLER'S team + user and answers the run's pending plan-confirmation card via <see cref="IWorkPlanConfirmationService"/>. Null (no pending confirmation / foreign run) → the controller 404-conflates.</summary>
public sealed class ConfirmRunPlanCommandHandler : IRequestHandler<ConfirmRunPlanCommand, WorkPlanConfirmationOutcome?>
{
    private readonly IWorkPlanConfirmationService _confirmations;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ConfirmRunPlanCommandHandler(IWorkPlanConfirmationService confirmations, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _confirmations = confirmations;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<WorkPlanConfirmationOutcome?> Handle(ConfirmRunPlanCommand request, CancellationToken cancellationToken) =>
        await _confirmations.AnswerAsync(request.RunId, _currentTeam.Id!.Value, _currentUser.Id!.Value, request.Approve, request.Feedback, cancellationToken).ConfigureAwait(false);
}
