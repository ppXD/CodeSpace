using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class AddCredentialedModelCommandHandler : IRequestHandler<AddCredentialedModelCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public AddCredentialedModelCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(AddCredentialedModelCommand request, CancellationToken cancellationToken) =>
        await _service.AddModelAsync(request.ModelCredentialId, request.ModelId, request.DisplayName, cancellationToken).ConfigureAwait(false);
}
