using CodeSpace.Core.Services.Releases;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListTagsQueryHandler : IRequestHandler<ListTagsQuery, IReadOnlyList<RemoteTag>>
{
    private readonly IReleaseCatalogService _service;

    public ListTagsQueryHandler(IReleaseCatalogService service) { _service = service; }

    public async Task<IReadOnlyList<RemoteTag>> Handle(ListTagsQuery request, CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var perPage = Math.Clamp(request.PerPage, 1, ListTagsQuery.MaxPerPage);
        return await _service.ListTagsAsync(request.RepositoryId, page, perPage, cancellationToken).ConfigureAwait(false);
    }
}
