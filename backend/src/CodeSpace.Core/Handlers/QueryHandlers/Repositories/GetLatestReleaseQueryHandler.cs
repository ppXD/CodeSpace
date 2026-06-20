using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetLatestReleaseQueryHandler : IRequestHandler<GetLatestReleaseQuery, RemoteRelease?>
{
    private readonly IRepositoryInsightsService _service;

    public GetLatestReleaseQueryHandler(IRepositoryInsightsService service) { _service = service; }

    public async Task<RemoteRelease?> Handle(GetLatestReleaseQuery request, CancellationToken cancellationToken) =>
        await _service.GetLatestReleaseAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}
