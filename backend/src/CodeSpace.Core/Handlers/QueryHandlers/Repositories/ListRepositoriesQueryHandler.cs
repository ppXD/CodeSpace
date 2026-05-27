using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListRepositoriesQueryHandler : IRequestHandler<ListRepositoriesQuery, IReadOnlyList<RepositorySummary>>
{
    private readonly IRepositoryService _service;

    public ListRepositoriesQueryHandler(IRepositoryService service) { _service = service; }

    public async Task<IReadOnlyList<RepositorySummary>> Handle(ListRepositoriesQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(request.ProviderInstanceId, request.ProjectId, cancellationToken).ConfigureAwait(false);
}
