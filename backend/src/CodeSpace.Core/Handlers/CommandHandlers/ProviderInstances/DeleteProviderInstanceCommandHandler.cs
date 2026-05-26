using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Commands.ProviderInstances;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ProviderInstances;

public sealed class DeleteProviderInstanceCommandHandler : IRequestHandler<DeleteProviderInstanceCommand, DeleteProviderInstanceResult>
{
    private readonly IProviderInstanceService _service;

    public DeleteProviderInstanceCommandHandler(IProviderInstanceService service) { _service = service; }

    public async Task<DeleteProviderInstanceResult> Handle(DeleteProviderInstanceCommand request, CancellationToken cancellationToken) =>
        await _service.DeleteAsync(request.ProviderInstanceId, request.Force, cancellationToken).ConfigureAwait(false);
}
