using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class ImportPackArtifactsCommandHandler : IRequestHandler<ImportPackArtifactsCommand, PackImportResult>
{
    private readonly IPackImportService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ImportPackArtifactsCommandHandler(IPackImportService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<PackImportResult> Handle(ImportPackArtifactsCommand request, CancellationToken cancellationToken) =>
        _service.ImportFromPackAsync(_currentTeam.Id!.Value, request.PackId, request.SourcePaths, _currentUser.Id!.Value, cancellationToken);
}
