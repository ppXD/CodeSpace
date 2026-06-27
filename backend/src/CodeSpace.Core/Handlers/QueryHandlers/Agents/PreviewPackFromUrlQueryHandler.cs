using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class PreviewPackFromUrlQueryHandler : IRequestHandler<PreviewPackFromUrlQuery, PackPreview>
{
    private readonly IPackImportService _service;
    private readonly ICurrentTeam _currentTeam;

    public PreviewPackFromUrlQueryHandler(IPackImportService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<PackPreview> Handle(PreviewPackFromUrlQuery request, CancellationToken cancellationToken) =>
        _service.PreviewFromUrlAsync(request.Url, request.Reference, _currentTeam.Id!.Value, cancellationToken);
}
