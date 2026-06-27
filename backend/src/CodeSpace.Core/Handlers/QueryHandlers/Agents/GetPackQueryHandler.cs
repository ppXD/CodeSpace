using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class GetPackQueryHandler : IRequestHandler<GetPackQuery, PackDetail?>
{
    private readonly IPackService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetPackQueryHandler(IPackService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<PackDetail?> Handle(GetPackQuery request, CancellationToken cancellationToken) =>
        _service.GetAsync(_currentTeam.Id!.Value, request.PackId, cancellationToken);
}
