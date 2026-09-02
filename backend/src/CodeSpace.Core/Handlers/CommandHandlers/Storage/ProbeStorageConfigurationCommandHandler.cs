using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class ProbeStorageConfigurationCommandHandler : IRequestHandler<ProbeStorageConfigurationCommand, StorageConfigurationProbeResult>
{
    private readonly IStorageConfigurationProbeService _service;

    public ProbeStorageConfigurationCommandHandler(IStorageConfigurationProbeService service) { _service = service; }

    public Task<StorageConfigurationProbeResult> Handle(ProbeStorageConfigurationCommand request, CancellationToken cancellationToken) =>
        _service.ProbeAsync(new StorageConfigurationProbeRequest(request.ProviderTypeKey, request.NonSecretConfig, request.Secret), cancellationToken);
}
