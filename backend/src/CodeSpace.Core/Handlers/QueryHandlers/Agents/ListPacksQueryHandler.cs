using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListPacksQueryHandler : IRequestHandler<ListPacksQuery, IReadOnlyList<PackSummary>>
{
    private readonly IPackService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListPacksQueryHandler(IPackService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<IReadOnlyList<PackSummary>> Handle(ListPacksQuery request, CancellationToken cancellationToken) =>
        _service.ListAsync(_currentTeam.Id!.Value, cancellationToken);
}
