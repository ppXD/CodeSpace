using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

public sealed class ListStorageProfilesQueryHandler : IRequestHandler<ListStorageProfilesQuery, IReadOnlyList<StorageProfileSummary>>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageProfilesQueryHandler(IStorageProfileService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<StorageProfileSummary>> Handle(ListStorageProfilesQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(_currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class ListStorageProfilePageQueryHandler : IRequestHandler<ListStorageProfilePageQuery, StoragePage<StorageProfileSummary>>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageProfilePageQueryHandler(IStorageProfileService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StoragePage<StorageProfileSummary>> Handle(ListStorageProfilePageQuery request, CancellationToken cancellationToken) =>
        await _service.ListPageAsync(_currentTeam.Id!.Value, request.Cursor, request.Limit, cancellationToken).ConfigureAwait(false);
}

public sealed class GetStorageProfileQueryHandler : IRequestHandler<GetStorageProfileQuery, StorageProfileDetail?>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetStorageProfileQueryHandler(IStorageProfileService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StorageProfileDetail?> Handle(GetStorageProfileQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(_currentTeam.Id!.Value, request.ProfileId, cancellationToken).ConfigureAwait(false);
}
