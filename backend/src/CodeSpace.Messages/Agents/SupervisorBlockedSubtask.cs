namespace CodeSpace.Messages.Agents;

/// <summary>
/// One subtask a spawn WITHHELD rather than staged, with the server's reason (Rule 18.1 noun) — read off a spawn
/// outcome's <c>blockedSubtasks</c> array by <c>SupervisorOutcome.ReadBlockedSubtasks</c>.
///
/// <para>The block is written by <c>RealSupervisorActionExecutor.BuildBlockedSpawnOutcome</c> when dependency
/// staging refuses to hand off silently — a producer carrying no branch, no patch artifact and no inline patch, or producers
/// whose work could not be auto-integrated. The spawn stages ZERO agents in that case, so without this the decider
/// sees only an empty fan-out and cannot tell a withheld unit from one that simply had nothing to do.</para>
/// </summary>
public sealed record SupervisorBlockedSubtask
{
    /// <summary>The plan-local subtask id that was withheld.</summary>
    public required string SubtaskId { get; init; }

    /// <summary>The server's reason for withholding it — never a silent default-branch clone over real work.</summary>
    public required string Reason { get; init; }
}
