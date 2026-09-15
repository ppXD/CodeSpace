using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// P2a-4: sweep recent terminal contract-era runs into durable shadow assessments (composer chain; Lock Clause 1 — never mutates a terminal).
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): a run whose assessment fails stays a candidate and the
/// sweep carries on. One command transaction around the whole tick would let a single bad row undo every other row's
/// work.</para>
/// </summary>
public sealed record SweepCompletionShadowCommand : ICommand<int>, INonTransactionalCommand
{
    /// <summary>Runs examined per sweep — bounds each tick's compose cost.</summary>
    public int BatchSize { get; init; } = 50;
}
