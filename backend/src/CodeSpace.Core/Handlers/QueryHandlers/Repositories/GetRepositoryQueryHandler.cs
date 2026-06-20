using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetRepositoryQueryHandler : IRequestHandler<GetRepositoryQuery, RepositoryDetail?>
{
    private readonly IRepositoryService _service;

    public GetRepositoryQueryHandler(IRepositoryService service) { _service = service; }

    public async Task<RepositoryDetail?> Handle(GetRepositoryQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(request.RepositoryId, request.Refresh, cancellationToken).ConfigureAwait(false);
}
