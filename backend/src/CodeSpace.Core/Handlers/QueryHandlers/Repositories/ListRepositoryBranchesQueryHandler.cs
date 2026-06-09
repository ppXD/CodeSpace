using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListRepositoryBranchesQueryHandler : IRequestHandler<ListRepositoryBranchesQuery, IReadOnlyList<RemoteBranch>>
{
    private readonly IRepositorySourceService _service;

    public ListRepositoryBranchesQueryHandler(IRepositorySourceService service) { _service = service; }

    public async Task<IReadOnlyList<RemoteBranch>> Handle(ListRepositoryBranchesQuery request, CancellationToken cancellationToken) =>
        await _service.ListBranchesAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}
