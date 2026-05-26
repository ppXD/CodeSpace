using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class CompleteCredentialOAuthCommandHandler : IRequestHandler<CompleteCredentialOAuthCommand, CompleteCredentialOAuthResult>
{
    private readonly IOAuthFlowService _flow;

    public CompleteCredentialOAuthCommandHandler(IOAuthFlowService flow) { _flow = flow; }

    public async Task<CompleteCredentialOAuthResult> Handle(CompleteCredentialOAuthCommand request, CancellationToken cancellationToken) =>
        await _flow.CompleteAsync(request.State, request.Code, cancellationToken).ConfigureAwait(false);
}
