using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>Rule 16 — thin handler. The restore lives in <see cref="ISealedPayloadUnsealService"/>.</summary>
public sealed class UnsealNonTerminalPayloadsCommandHandler : IRequestHandler<UnsealNonTerminalPayloadsCommand, UnsealNonTerminalPayloadsResponse>
{
    private readonly ISealedPayloadUnsealService _unseal;

    public UnsealNonTerminalPayloadsCommandHandler(ISealedPayloadUnsealService unseal) { _unseal = unseal; }

    public async Task<UnsealNonTerminalPayloadsResponse> Handle(UnsealNonTerminalPayloadsCommand request, CancellationToken cancellationToken) =>
        await _unseal.UnsealNonTerminalAsync(cancellationToken).ConfigureAwait(false);
}
