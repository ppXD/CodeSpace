using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class InitCredentialOAuthCommandHandler : IRequestHandler<InitCredentialOAuthCommand, InitCredentialOAuthResult>
{
    private readonly IOAuthFlowService _flow;

    public InitCredentialOAuthCommandHandler(IOAuthFlowService flow) { _flow = flow; }

    public async Task<InitCredentialOAuthResult> Handle(InitCredentialOAuthCommand request, CancellationToken cancellationToken) =>
        await _flow.InitAsync(request.ProviderInstanceId, request.DisplayName, request.IntendedOwnerUserId, request.ReturnUrl, request.Scopes, cancellationToken).ConfigureAwait(false);
}
