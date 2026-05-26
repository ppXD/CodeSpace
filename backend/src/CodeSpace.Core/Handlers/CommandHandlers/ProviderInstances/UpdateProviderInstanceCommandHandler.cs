using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Commands.ProviderInstances;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ProviderInstances;

public sealed class UpdateProviderInstanceCommandHandler : IRequestHandler<UpdateProviderInstanceCommand, Unit>
{
    private readonly IProviderInstanceService _service;

    public UpdateProviderInstanceCommandHandler(IProviderInstanceService service) { _service = service; }

    public async Task<Unit> Handle(UpdateProviderInstanceCommand request, CancellationToken cancellationToken)
    {
        await _service.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
