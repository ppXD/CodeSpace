using CodeSpace.Core.Services.ReleaseCatalog;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetReleaseQueryHandler : IRequestHandler<GetReleaseQuery, RemoteRelease>
{
    private readonly IReleaseCatalogService _service;

    public GetReleaseQueryHandler(IReleaseCatalogService service) { _service = service; }

    public async Task<RemoteRelease> Handle(GetReleaseQuery request, CancellationToken cancellationToken) =>
        await _service.GetReleaseAsync(request.RepositoryId, request.Tag, cancellationToken).ConfigureAwait(false);
}
