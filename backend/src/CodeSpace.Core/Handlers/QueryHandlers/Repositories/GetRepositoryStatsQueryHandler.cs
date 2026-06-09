using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetRepositoryStatsQueryHandler : IRequestHandler<GetRepositoryStatsQuery, RemoteRepositoryStats>
{
    private readonly IRepositoryInsightsService _service;

    public GetRepositoryStatsQueryHandler(IRepositoryInsightsService service) { _service = service; }

    public async Task<RemoteRepositoryStats> Handle(GetRepositoryStatsQuery request, CancellationToken cancellationToken) =>
        await _service.GetStatsAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}
