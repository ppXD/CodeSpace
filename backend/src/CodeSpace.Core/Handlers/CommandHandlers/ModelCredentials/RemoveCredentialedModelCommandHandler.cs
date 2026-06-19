using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class RemoveCredentialedModelCommandHandler : IRequestHandler<RemoveCredentialedModelCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public RemoveCredentialedModelCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(RemoveCredentialedModelCommand request, CancellationToken cancellationToken) =>
        await _service.RemoveModelAsync(request.ModelCredentialId, request.ModelRowId, cancellationToken).ConfigureAwait(false);
}
