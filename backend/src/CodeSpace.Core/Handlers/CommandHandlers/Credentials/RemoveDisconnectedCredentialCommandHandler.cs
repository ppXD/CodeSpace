using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class RemoveDisconnectedCredentialCommandHandler : IRequestHandler<RemoveDisconnectedCredentialCommand, RemoveDisconnectedCredentialResult>
{
    private readonly ICredentialService _credentials;

    public RemoveDisconnectedCredentialCommandHandler(ICredentialService credentials) { _credentials = credentials; }

    public async Task<RemoveDisconnectedCredentialResult> Handle(RemoveDisconnectedCredentialCommand request, CancellationToken cancellationToken) =>
        new() { CredentialId = await _credentials.RemoveDisconnectedAsync(request.CredentialId, cancellationToken).ConfigureAwait(false) };
}
