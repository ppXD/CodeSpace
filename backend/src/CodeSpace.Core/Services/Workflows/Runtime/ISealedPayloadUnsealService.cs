using CodeSpace.Messages.Commands.Workflows;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>The pre-rollback restore of sealed payloads (<see cref="UnsealNonTerminalPayloadsCommand"/>): every non-terminal agent task and run payload that holds ciphertext goes back to the exact, unsealed row a previous-version worker executes.</summary>
public interface ISealedPayloadUnsealService
{
    Task<UnsealNonTerminalPayloadsResponse> UnsealNonTerminalAsync(CancellationToken cancellationToken);
}
