using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetRepositoryTreeCommitsQueryHandler : IRequestHandler<GetRepositoryTreeCommitsQuery, IReadOnlyDictionary<string, RemoteCommitSummary>>
{
    private readonly IRepositoryHistoryService _service;

    public GetRepositoryTreeCommitsQueryHandler(IRepositoryHistoryService service) { _service = service; }

    public async Task<IReadOnlyDictionary<string, RemoteCommitSummary>> Handle(GetRepositoryTreeCommitsQuery request, CancellationToken cancellationToken) =>
        await _service.GetTreeCommitsAsync(request.RepositoryId, request.Paths, request.Ref, cancellationToken).ConfigureAwait(false);
}
