using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// Lifecycle status of one durable <c>ToolCallLedger</c> row — the exactly-once + audit record of a side-effecting
/// MCP tool call. A row is born <see cref="Pending"/> (the caller claimed the right to run the side effect) and moves
/// to a terminal: <see cref="Succeeded"/> / <see cref="Failed"/> / <see cref="Denied"/> when the call resolves
/// synchronously, or pauses at <see cref="AwaitingApproval"/> (durable mid-turn HITL — wired by item D), is then
/// claimed for execution at <see cref="Running"/> by exactly one executor, and lands at <see cref="Succeeded"/> /
/// <see cref="Failed"/> (or <see cref="Expired"/> when the reaper expires an undecided approval).
///
/// <para>Lives in Messages (like <see cref="AgentRunStatus"/>) so both the persistence entity and the service/DTO
/// layers reference it without a backwards layer dependency. Serialized as its string name
/// (<see cref="JsonStringEnumConverter"/>, matching <c>AgentRunStatus</c>) and stored as its string name in the
/// ledger's <c>status</c> column. The <see cref="AwaitingApproval"/> / <see cref="Running"/> / <see cref="Expired"/>
/// states are present for item D (durable approval) and are unused by the C ledger vertical — no C-path row reaches them.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolCallLedgerStatus
{
    Pending,
    Succeeded,
    Failed,

    /// <summary>A gate-denied call. Unused by item C — the gate short-circuits BEFORE the ledger branch, so a denial writes no audit row yet; denial-auditing is deferred to the later audit slice.</summary>
    Denied,

    AwaitingApproval,

    /// <summary>An approved approval row claimed for execution by exactly one executor (the single-winner CAS out of <see cref="AwaitingApproval"/>) — the side effect is being run RIGHT NOW. Only the executor that won this CAS calls the tool; a concurrent racer that lost re-reads and replays. NOT terminal: it flips to <see cref="Succeeded"/> / <see cref="Failed"/> once the side effect resolves. Item D.</summary>
    Running,

    Expired,
}
