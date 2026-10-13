using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class CompleteCredentialOAuthAsInitiatorCommandHandler : IRequestHandler<CompleteCredentialOAuthAsInitiatorCommand, CompleteCredentialOAuthResult>
{
    private readonly IOAuthFlowService _flow;

    public CompleteCredentialOAuthAsInitiatorCommandHandler(IOAuthFlowService flow) { _flow = flow; }

    public async Task<CompleteCredentialOAuthResult> Handle(CompleteCredentialOAuthAsInitiatorCommand request, CancellationToken cancellationToken) =>
        await _flow.CompleteAsInitiatorAsync(request.State, request.Code, cancellationToken).ConfigureAwait(false);
}
