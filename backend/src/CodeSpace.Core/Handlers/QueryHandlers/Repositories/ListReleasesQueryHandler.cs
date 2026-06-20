using CodeSpace.Core.Services.Releases;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListReleasesQueryHandler : IRequestHandler<ListReleasesQuery, IReadOnlyList<RemoteRelease>>
{
    private readonly IReleaseCatalogService _service;

    public ListReleasesQueryHandler(IReleaseCatalogService service) { _service = service; }

    public async Task<IReadOnlyList<RemoteRelease>> Handle(ListReleasesQuery request, CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var perPage = Math.Clamp(request.PerPage, 1, ListReleasesQuery.MaxPerPage);
        return await _service.ListReleasesAsync(request.RepositoryId, page, perPage, cancellationToken).ConfigureAwait(false);
    }
}
