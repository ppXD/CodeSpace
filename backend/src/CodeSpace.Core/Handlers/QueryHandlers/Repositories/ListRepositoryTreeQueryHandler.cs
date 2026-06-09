using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListRepositoryTreeQueryHandler : IRequestHandler<ListRepositoryTreeQuery, IReadOnlyList<RemoteTreeEntry>>
{
    private readonly IRepositorySourceService _service;

    public ListRepositoryTreeQueryHandler(IRepositorySourceService service) { _service = service; }

    public async Task<IReadOnlyList<RemoteTreeEntry>> Handle(ListRepositoryTreeQuery request, CancellationToken cancellationToken) =>
        await _service.ListTreeAsync(request.RepositoryId, request.Path, request.Ref, cancellationToken).ConfigureAwait(false);
}
