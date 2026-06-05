using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class RespondToMessageCommandHandler : IRequestHandler<RespondToMessageCommand, Unit>
{
    private readonly IMessageInteractionService _interactions;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public RespondToMessageCommandHandler(IMessageInteractionService interactions, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _interactions = interactions;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<Unit> Handle(RespondToMessageCommand request, CancellationToken cancellationToken)
    {
        await _interactions.RespondAsync(_currentTeam.Id!.Value, request.MessageId, request.ResponseKey, _currentUser.Id!.Value, request.Comment, request.Values, cancellationToken);
        return Unit.Value;
    }
}
