using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Run one nightly lesson-distillation round over every team with fresh failed/parked runs — fired by the
/// recurring learning job; can also be sent ad-hoc from a test. NOT tenant-scoped: a system-wide enrichment that
/// runs without an actor context (mirrors <c>TierStaleModelCapabilitiesCommand</c>). Returns the number of teams
/// distilled for log surfacing.
/// </summary>
public sealed record DistillLessonsCommand : ICommand<DistillLessonsResponse>;

/// <summary>Count of teams whose fresh failures this round distilled (0 in steady state).</summary>
public sealed record DistillLessonsResponse
{
    public required int TeamsDistilled { get; init; }
}
