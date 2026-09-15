using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// W-hard 2b: settle folded agent-attempt reservations at the priced actual, release terminal orphans, expire the overdue.
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): a run whose settlement fails keeps its reservations live
/// for the next pass while every other run still settles. One command transaction around the whole tick would let a
/// single bad row undo every other row's work.</para>
/// </summary>
public sealed record SweepBudgetSettlementCommand : ICommand<int>, INonTransactionalCommand
{
    public int BatchSize { get; init; } = 200;
}
