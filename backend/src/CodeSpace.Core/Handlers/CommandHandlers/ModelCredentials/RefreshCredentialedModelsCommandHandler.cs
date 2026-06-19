using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class RefreshCredentialedModelsCommandHandler : IRequestHandler<RefreshCredentialedModelsCommand, int>
{
    private readonly IModelCredentialService _service;

    public RefreshCredentialedModelsCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<int> Handle(RefreshCredentialedModelsCommand request, CancellationToken cancellationToken) =>
        await _service.RefreshModelsAsync(request.ModelCredentialId, cancellationToken).ConfigureAwait(false);
}
