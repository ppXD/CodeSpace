using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class RevokeCredentialCommandHandler : IRequestHandler<RevokeCredentialCommand, RevokeCredentialResult>
{
    private readonly ICredentialService _service;

    public RevokeCredentialCommandHandler(ICredentialService service) { _service = service; }

    public async Task<RevokeCredentialResult> Handle(RevokeCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.RevokeAsync(request.CredentialId, cancellationToken).ConfigureAwait(false);
}
