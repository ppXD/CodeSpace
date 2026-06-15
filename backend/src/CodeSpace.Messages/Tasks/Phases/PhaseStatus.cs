using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// The ONLY closed enum in the phase model — the pure UI render vocabulary a background-tasks board paints a
/// phase chip with. Every OTHER axis of a <see cref="RunPhase"/> (the phase <c>Kind</c>, an agent <c>Status</c>,
/// the run status) stays an OPEN string so a new node kind / decision verb / status name never forces a wire-shape
/// change. This is closed on purpose: it is a rendering concern, not a domain fact, and the renderer must handle
/// exactly these six. Serialized as its string name (<see cref="JsonStringEnumConverter"/>) so the wire is
/// forward-tolerant — an older client deserializing a name it doesn't know degrades, never throws.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PhaseStatus
{
    /// <summary>Not started yet (a node the engine has frontiered but not run; a decision still claimed-Pending).</summary>
    Pending,

    /// <summary>In flight (a Running node; a decision claimed for execution).</summary>
    Active,

    /// <summary>Parked on an external signal (a Suspended node; a decision awaiting human approval / an unanswered ask_human).</summary>
    Waiting,

    Succeeded,
    Failed,

    /// <summary>Deliberately not executed (a Skipped node — a branch the engine pruned).</summary>
    Skipped,
}
