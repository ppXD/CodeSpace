using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class SetDefaultCredentialedModelCommandHandler : IRequestHandler<SetDefaultCredentialedModelCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public SetDefaultCredentialedModelCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(SetDefaultCredentialedModelCommand request, CancellationToken cancellationToken) =>
        await _service.SetDefaultModelAsync(request.ModelCredentialId, request.ModelRowId, cancellationToken).ConfigureAwait(false);
}
