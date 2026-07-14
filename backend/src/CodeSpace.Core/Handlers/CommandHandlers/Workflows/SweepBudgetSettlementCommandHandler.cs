using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

public sealed class SweepBudgetSettlementCommandHandler : IRequestHandler<SweepBudgetSettlementCommand, int>
{
    private readonly IBudgetSettlementService _settlement;

    public SweepBudgetSettlementCommandHandler(IBudgetSettlementService settlement) { _settlement = settlement; }

    public async Task<int> Handle(SweepBudgetSettlementCommand request, CancellationToken cancellationToken)
    {
        var (settled, released, expired) = await _settlement.SweepAsync(request.BatchSize, cancellationToken).ConfigureAwait(false);
        return settled + released + expired;
    }
}
