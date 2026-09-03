using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class SetCredentialedModelPriceCommandHandler : IRequestHandler<SetCredentialedModelPriceCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public SetCredentialedModelPriceCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(SetCredentialedModelPriceCommand request, CancellationToken cancellationToken) =>
        await _service.SetModelPriceAsync(request.ModelCredentialId, request.ModelRowId, CodeSpace.Messages.Agents.ModelPrice.FromNullable(request.InputUsdPerMillion, request.OutputUsdPerMillion), cancellationToken).ConfigureAwait(false);
}
