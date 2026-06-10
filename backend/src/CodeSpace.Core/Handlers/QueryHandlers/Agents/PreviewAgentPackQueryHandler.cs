using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class PreviewAgentPackQueryHandler : IRequestHandler<PreviewAgentPackQuery, AgentPackPreview>
{
    private readonly IAgentPackImportService _service;
    private readonly ICurrentTeam _currentTeam;

    public PreviewAgentPackQueryHandler(IAgentPackImportService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<AgentPackPreview> Handle(PreviewAgentPackQuery request, CancellationToken cancellationToken) =>
        _service.PreviewAsync(request.RepositoryId, request.Reference, request.RootPath, _currentTeam.Id!.Value, cancellationToken);
}
