using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetRepositoryLanguagesQueryHandler : IRequestHandler<GetRepositoryLanguagesQuery, IReadOnlyList<RemoteLanguage>>
{
    private readonly IRepositoryInsightsService _service;

    public GetRepositoryLanguagesQueryHandler(IRepositoryInsightsService service) { _service = service; }

    public async Task<IReadOnlyList<RemoteLanguage>> Handle(GetRepositoryLanguagesQuery request, CancellationToken cancellationToken) =>
        await _service.GetLanguagesAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}
