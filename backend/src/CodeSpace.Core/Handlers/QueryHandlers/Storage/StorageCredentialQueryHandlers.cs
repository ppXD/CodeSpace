using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

public sealed class ListStorageCredentialsQueryHandler : IRequestHandler<ListStorageCredentialsQuery, IReadOnlyList<StorageCredentialMetadata>>
{
    private readonly IStorageCredentialService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageCredentialsQueryHandler(IStorageCredentialService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<StorageCredentialMetadata>> Handle(ListStorageCredentialsQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(_currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class ListStorageCredentialPageQueryHandler : IRequestHandler<ListStorageCredentialPageQuery, StoragePage<StorageCredentialMetadata>>
{
    private readonly IStorageCredentialService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageCredentialPageQueryHandler(IStorageCredentialService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StoragePage<StorageCredentialMetadata>> Handle(ListStorageCredentialPageQuery request, CancellationToken cancellationToken) =>
        await _service.ListPageAsync(_currentTeam.Id!.Value, request.Cursor, request.Limit, cancellationToken).ConfigureAwait(false);
}

public sealed class GetStorageCredentialQueryHandler : IRequestHandler<GetStorageCredentialQuery, StorageCredentialMetadata?>
{
    private readonly IStorageCredentialService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetStorageCredentialQueryHandler(IStorageCredentialService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StorageCredentialMetadata?> Handle(GetStorageCredentialQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(_currentTeam.Id!.Value, request.CredentialId, cancellationToken).ConfigureAwait(false);
}
