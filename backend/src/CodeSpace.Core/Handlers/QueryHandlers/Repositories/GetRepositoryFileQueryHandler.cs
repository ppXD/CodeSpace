using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetRepositoryFileQueryHandler : IRequestHandler<GetRepositoryFileQuery, RemoteFileContent>
{
    private readonly IRepositorySourceService _service;

    public GetRepositoryFileQueryHandler(IRepositorySourceService service) { _service = service; }

    public async Task<RemoteFileContent> Handle(GetRepositoryFileQuery request, CancellationToken cancellationToken) =>
        await _service.GetFileAsync(request.RepositoryId, request.Path, request.Ref, cancellationToken).ConfigureAwait(false);
}
