using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class ProbeStorageProfileCommandHandler : IRequestHandler<ProbeStorageProfileCommand, StorageProfileProbeResult>
{
    private readonly IStorageProfileProbeService _service;
    private readonly ICurrentTeam _currentTeam;

    public ProbeStorageProfileCommandHandler(IStorageProfileProbeService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public Task<StorageProfileProbeResult> Handle(ProbeStorageProfileCommand request, CancellationToken cancellationToken) =>
        _service.ProbeAsync(new StorageProfileProbeRequest(_currentTeam.Id!.Value, request.ProfileId, request.ProfileRevision, request.VerifyWriteAccess, Initialize: true), cancellationToken);
}
