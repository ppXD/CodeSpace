using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class SetCredentialedModelContextWindowCommandHandler : IRequestHandler<SetCredentialedModelContextWindowCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public SetCredentialedModelContextWindowCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(SetCredentialedModelContextWindowCommand request, CancellationToken cancellationToken) =>
        await _service.SetModelContextWindowAsync(request.ModelCredentialId, request.ModelRowId, request.ContextWindowTokens, cancellationToken).ConfigureAwait(false);
}
