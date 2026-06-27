using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

public sealed class ImportPackFromUrlCommandHandler : IRequestHandler<ImportPackFromUrlCommand, PackImportResult>
{
    private readonly IPackImportService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public ImportPackFromUrlCommandHandler(IPackImportService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public Task<PackImportResult> Handle(ImportPackFromUrlCommand request, CancellationToken cancellationToken) =>
        _service.ImportFromUrlAsync(request.Url, request.Reference, request.SourcePaths, _currentTeam.Id!.Value, _currentUser.Id!.Value, cancellationToken);
}
