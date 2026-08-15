using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class CreateStorageRouteCommandHandler : IRequestHandler<CreateStorageRouteCommand, StorageRouteDetail>
{
    private readonly IStorageRouteService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public CreateStorageRouteCommandHandler(IStorageRouteService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageRouteDetail> Handle(CreateStorageRouteCommand request, CancellationToken cancellationToken) =>
        await _service.CreateAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class AppendStorageRouteRevisionCommandHandler : IRequestHandler<AppendStorageRouteRevisionCommand, StorageRouteDetail?>
{
    private readonly IStorageRouteService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AppendStorageRouteRevisionCommandHandler(IStorageRouteService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageRouteDetail?> Handle(AppendStorageRouteRevisionCommand request, CancellationToken cancellationToken) =>
        await _service.AppendRevisionAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class SetStorageRouteStateCommandHandler : IRequestHandler<SetStorageRouteStateCommand, StorageRouteDetail?>
{
    private readonly IStorageRouteService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public SetStorageRouteStateCommandHandler(IStorageRouteService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageRouteDetail?> Handle(SetStorageRouteStateCommand request, CancellationToken cancellationToken) =>
        await _service.SetStateAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}
