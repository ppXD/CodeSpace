using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>W-hard 2b: settle folded agent-attempt reservations at the priced actual, release terminal orphans, expire the overdue.</summary>
public sealed record SweepBudgetSettlementCommand : ICommand<int>
{
    public int BatchSize { get; init; } = 200;
}
