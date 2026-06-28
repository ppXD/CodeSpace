using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListPackArtifactsQueryHandler : IRequestHandler<ListPackArtifactsQuery, PagedArtifacts>
{
    private readonly IPackService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListPackArtifactsQueryHandler(IPackService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<PagedArtifacts> Handle(ListPackArtifactsQuery request, CancellationToken cancellationToken) =>
        _service.ListArtifactsAsync(_currentTeam.Id!.Value, request.PackId, request.Kind, request.Search, request.Page, request.PageSize, cancellationToken);
}
