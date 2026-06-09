using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetRepositoryLatestCommitQueryHandler : IRequestHandler<GetRepositoryLatestCommitQuery, RemoteCommitSummary?>
{
    private readonly IRepositoryHistoryService _service;

    public GetRepositoryLatestCommitQueryHandler(IRepositoryHistoryService service) { _service = service; }

    public async Task<RemoteCommitSummary?> Handle(GetRepositoryLatestCommitQuery request, CancellationToken cancellationToken) =>
        await _service.GetLatestCommitAsync(request.RepositoryId, request.Path, request.Ref, cancellationToken).ConfigureAwait(false);
}
