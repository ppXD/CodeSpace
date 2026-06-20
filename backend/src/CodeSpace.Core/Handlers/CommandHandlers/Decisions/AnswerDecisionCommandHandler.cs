using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Decisions;

/// <summary>Thin dispatcher (Rule 16): resolves the team + answerer from identity (NEVER the body — fail-closed) and delegates the grain routing + resolve to the answer service.</summary>
public sealed class AnswerDecisionCommandHandler : IRequestHandler<AnswerDecisionCommand, AnswerDecisionResult>
{
    private readonly IDecisionAnswerService _answers;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AnswerDecisionCommandHandler(IDecisionAnswerService answers, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _answers = answers;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<AnswerDecisionResult> Handle(AnswerDecisionCommand request, CancellationToken cancellationToken) =>
        await _answers.AnswerAsync(request.DecisionId, request.SelectedOptions, request.FreeText, _currentTeam.Id!.Value, _currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);
}
