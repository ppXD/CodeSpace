using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class RelinkCredentialBindingsCommandHandler : IRequestHandler<RelinkCredentialBindingsCommand, RelinkCredentialBindingsResult>
{
    private readonly ICredentialRelinkService _service;

    public RelinkCredentialBindingsCommandHandler(ICredentialRelinkService service) { _service = service; }

    public async Task<RelinkCredentialBindingsResult> Handle(RelinkCredentialBindingsCommand request, CancellationToken cancellationToken) =>
        await _service.RelinkAllAsync(request.FromCredentialId, request.ToCredentialId, cancellationToken).ConfigureAwait(false);
}
