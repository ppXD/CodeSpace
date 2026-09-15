using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Run one nightly lesson lifecycle round: first reconcile exact success/negative exposure evidence, then distill fresh failed/parked runs — fired by the
/// recurring learning job; can also be sent ad-hoc from a test. NOT tenant-scoped: a system-wide enrichment that
/// runs without an actor context (mirrors <c>TierStaleModelCapabilitiesCommand</c>). Returns the number of teams
/// distilled for log surfacing.
///
/// <para>NOT transactional (<see cref="INonTransactionalCommand"/>): it opens a transaction of its OWN to hold the round's
/// advisory lock — one the pipeline's would refuse to nest — and distills each team independently. One command
/// transaction around the whole tick would let a single bad row undo every other row's work.</para>
/// </summary>
public sealed record DistillLessonsCommand : ICommand<DistillLessonsResponse>, INonTransactionalCommand;

/// <summary>Count of teams whose fresh failures this round distilled (0 in steady state).</summary>
public sealed record DistillLessonsResponse
{
    public required int TeamsDistilled { get; init; }
}
