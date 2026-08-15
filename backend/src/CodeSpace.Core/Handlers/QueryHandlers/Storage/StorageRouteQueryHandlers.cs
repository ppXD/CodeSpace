using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

public sealed class ListStorageRoutePageQueryHandler : IRequestHandler<ListStorageRoutePageQuery, StoragePage<StorageRouteSummary>>
{
    private readonly IStorageRouteService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageRoutePageQueryHandler(IStorageRouteService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StoragePage<StorageRouteSummary>> Handle(ListStorageRoutePageQuery request, CancellationToken cancellationToken) =>
        await _service.ListPageAsync(_currentTeam.Id!.Value, request.Cursor, request.Limit, cancellationToken).ConfigureAwait(false);
}

public sealed class GetStorageRouteQueryHandler : IRequestHandler<GetStorageRouteQuery, StorageRouteDetail?>
{
    private readonly IStorageRouteService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetStorageRouteQueryHandler(IStorageRouteService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StorageRouteDetail?> Handle(GetStorageRouteQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(_currentTeam.Id!.Value, request.RouteId, request.RevisionCursor, request.RevisionLimit, cancellationToken).ConfigureAwait(false);
}
