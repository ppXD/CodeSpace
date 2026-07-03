namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// A planned subtask still BLOCKED by an unmet dependency at a wave — the dependency-gated "waiting on #n" the journal
/// shows alongside a spawn, so a reader sees which parts of the plan the DAG wasn't ready to run yet (a unit runs only
/// once its predecessors are an accepted success). This is the plan's blocked FRONTIER as of that wave — a not-yet-ready
/// subtask — NOT necessarily one this particular spawn requested. <see cref="WaitingOn"/> is the unmet dependency subtask
/// ids. Empty for a flat plan (no DAG → nothing is ever blocked).
/// </summary>
public sealed record JournalDeferredSubtask
{
    /// <summary>The plan-local id of the still-blocked subtask (a dependency isn't an accepted success yet).</summary>
    public required string SubtaskId { get; init; }

    /// <summary>The dependency subtask ids it is still waiting on (not yet an accepted success).</summary>
    public required IReadOnlyList<string> WaitingOn { get; init; }
}
