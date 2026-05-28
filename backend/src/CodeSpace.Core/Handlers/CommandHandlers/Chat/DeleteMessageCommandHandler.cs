using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class DeleteMessageCommandHandler : IRequestHandler<DeleteMessageCommand>
{
    private readonly IMessageService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public DeleteMessageCommandHandler(IMessageService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task Handle(DeleteMessageCommand request, CancellationToken cancellationToken) =>
        _service.DeleteAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.MessageId, cancellationToken);
}
