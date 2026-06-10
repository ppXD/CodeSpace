using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class ImportAgentPackCommandHandler : IRequestHandler<ImportAgentPackCommand, IReadOnlyList<AgentImportResult>>
{
    private readonly IAgentPackImportService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ImportAgentPackCommandHandler(IAgentPackImportService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<IReadOnlyList<AgentImportResult>> Handle(ImportAgentPackCommand request, CancellationToken cancellationToken) =>
        _service.ImportAsync(request.RepositoryId, request.Reference, request.RootPath, request.SelectedSourcePaths, _currentTeam.Id!.Value, _currentUser.Id!.Value, cancellationToken);
}
