using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// Lifecycle status of one durable <c>SupervisorDecisionRecord</c> row — the exactly-once + replayable record of a
/// supervisor's emitted decision. Mirrors <see cref="ToolCallLedgerStatus"/>. A row is born <see cref="Pending"/> (the
/// caller claimed the right to emit + execute the decision) and either resolves synchronously to a terminal
/// (<see cref="Succeeded"/> / <see cref="Failed"/>), or pauses at <see cref="AwaitingApproval"/> (durable mid-turn HITL —
/// wired by a later slice), is then claimed for execution at <see cref="Running"/> by exactly one executor, and lands at
/// <see cref="Succeeded"/> / <see cref="Failed"/> (or <see cref="Expired"/> when the reaper sweeps a stale undecided row).
///
/// <para>Lives in Messages (like <see cref="ToolCallLedgerStatus"/>) so the persistence entity, the state machine, and
/// the service all reference it without a backwards layer dependency. Serialized as its string name
/// (<see cref="JsonStringEnumConverter"/>, matching <c>ToolCallLedgerStatus</c>) and stored as its string name in the
/// ledger's <c>status</c> column. The <see cref="AwaitingApproval"/> state is reserved for the HITL slice and unused by
/// E1 (pure substrate) — no E1-path row reaches it.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorDecisionStatus
{
    Pending,

    /// <summary>A decision parked for human approval (durable mid-turn HITL — a later slice). Reserved; unused by E1.</summary>
    AwaitingApproval,

    /// <summary>The decision claimed for execution by exactly one executor (the single-winner CAS out of <see cref="Pending"/> / <see cref="AwaitingApproval"/>) — the side effect runs RIGHT NOW. Only the executor that won this CAS executes the decision; a concurrent racer that lost re-reads and replays. NOT terminal: it flips to <see cref="Succeeded"/> / <see cref="Failed"/> once the decision resolves.</summary>
    Running,

    Succeeded,
    Failed,

    /// <summary>A stale undecided row the reaper swept past its retention window (still <see cref="Pending"/>). Terminal.</summary>
    Expired,
}
