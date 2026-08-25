using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Defaults;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

/// <summary>
/// Deployment-template writes. They take no team on purpose — the template describes the whole deployment — so unlike
/// every team-scoped storage handler these never touch <c>ICurrentTeam</c>. Nothing consumes what they write yet.
/// </summary>
public sealed class CreateStorageDefaultCommandHandler : IRequestHandler<CreateStorageDefaultCommand, StorageDefaultDetail>
{
    private readonly IStorageDefaultService _service;
    private readonly ICurrentUser _currentUser;

    public CreateStorageDefaultCommandHandler(IStorageDefaultService service, ICurrentUser currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    public async Task<StorageDefaultDetail> Handle(CreateStorageDefaultCommand request, CancellationToken cancellationToken) =>
        await _service.CreateAsync(_currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class UpdateStorageDefaultCommandHandler : IRequestHandler<UpdateStorageDefaultCommand, StorageDefaultDetail?>
{
    private readonly IStorageDefaultService _service;
    private readonly ICurrentUser _currentUser;

    public UpdateStorageDefaultCommandHandler(IStorageDefaultService service, ICurrentUser currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    public async Task<StorageDefaultDetail?> Handle(UpdateStorageDefaultCommand request, CancellationToken cancellationToken) =>
        await _service.UpdateAsync(_currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class SetStorageDefaultEnabledCommandHandler : IRequestHandler<SetStorageDefaultEnabledCommand, StorageDefaultDetail?>
{
    private readonly IStorageDefaultService _service;
    private readonly ICurrentUser _currentUser;

    public SetStorageDefaultEnabledCommandHandler(IStorageDefaultService service, ICurrentUser currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    public async Task<StorageDefaultDetail?> Handle(SetStorageDefaultEnabledCommand request, CancellationToken cancellationToken) =>
        await _service.SetEnabledAsync(_currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}
