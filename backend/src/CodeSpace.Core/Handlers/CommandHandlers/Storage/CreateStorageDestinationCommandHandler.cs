using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Destinations;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class CreateStorageDestinationCommandHandler : IRequestHandler<CreateStorageDestinationCommand, StorageDestinationDetail>
{
    private readonly IStorageDestinationService _service;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;

    public CreateStorageDestinationCommandHandler(IStorageDestinationService service, ICurrentUser currentUser, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
    }

    public Task<StorageDestinationDetail> Handle(CreateStorageDestinationCommand request, CancellationToken cancellationToken) =>
        _service.CreateAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken);
}
