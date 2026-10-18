using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class ReplaceCredentialTokenCommandHandler : IRequestHandler<ReplaceCredentialTokenCommand, ReplaceCredentialTokenResult>
{
    private readonly ICredentialService _credentials;

    public ReplaceCredentialTokenCommandHandler(ICredentialService credentials) { _credentials = credentials; }

    public async Task<ReplaceCredentialTokenResult> Handle(ReplaceCredentialTokenCommand request, CancellationToken cancellationToken) =>
        await _credentials.ReplaceTokenAsync(request.CredentialId, request.Token, cancellationToken).ConfigureAwait(false);
}
