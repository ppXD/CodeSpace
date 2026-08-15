using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class CreateStorageCredentialCommandHandler : IRequestHandler<CreateStorageCredentialCommand, StorageCredentialMetadata>
{
    private readonly IStorageCredentialService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public CreateStorageCredentialCommandHandler(IStorageCredentialService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageCredentialMetadata> Handle(CreateStorageCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.CreateAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class AppendStorageCredentialRevisionCommandHandler : IRequestHandler<AppendStorageCredentialRevisionCommand, StorageCredentialMetadata?>
{
    private readonly IStorageCredentialService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AppendStorageCredentialRevisionCommandHandler(IStorageCredentialService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageCredentialMetadata?> Handle(AppendStorageCredentialRevisionCommand request, CancellationToken cancellationToken) =>
        await _service.AppendRevisionAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class RevokeStorageCredentialCommandHandler : IRequestHandler<RevokeStorageCredentialCommand, StorageCredentialMetadata?>
{
    private readonly IStorageCredentialService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public RevokeStorageCredentialCommandHandler(IStorageCredentialService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageCredentialMetadata?> Handle(RevokeStorageCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.RevokeAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}
