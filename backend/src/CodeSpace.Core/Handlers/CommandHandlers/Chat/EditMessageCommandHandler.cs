using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Dtos.Chat;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Chat;

public sealed class EditMessageCommandHandler : IRequestHandler<EditMessageCommand, MessageView>
{
    private readonly IMessageService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public EditMessageCommandHandler(IMessageService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<MessageView> Handle(EditMessageCommand request, CancellationToken cancellationToken) =>
        _service.EditAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.MessageId, request.Body, cancellationToken);
}
