using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// A4: fill the durable <c>run_scorecard</c> row for terminal contract-era runs that lack one. The primary writer
/// is the completion-shadow sweep, which every NEW terminal run passes through; this backfill exists for the runs
/// that terminalized before the table did (and for one whose write failed once). Idempotent and bounded — a run
/// with a row is not a candidate, so the backlog only ever shrinks. NOT tenant-scoped: a system-wide projection
/// that runs without an actor context (mirrors <c>SweepCompletionShadowCommand</c>).
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): a run whose scorecard fails stays a candidate and the
/// pass carries on. One command transaction around the whole tick would let a single bad row undo every other row's
/// work.</para>
/// </summary>
public sealed record BackfillRunScorecardsCommand : ICommand<int>, INonTransactionalCommand
{
    /// <summary>Runs projected per tick — bounds each pass.</summary>
    public int BatchSize { get; init; } = 50;
}
