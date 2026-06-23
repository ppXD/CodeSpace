using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks.Timeline;

/// <summary>
/// A timeline event's NARRATIVE prominence — the second closed axis (alongside <see cref="TimelineSeverity"/>) the UI
/// is allowed to switch on. A <see cref="Milestone"/> is a story beat the operator reads at a glance (run started/done,
/// a node failed, a supervisor decision, an agent's final summary); a <see cref="Detail"/> is structural churn (a node
/// started/completed, a file edit) the UI FOLDS into a "N steps" disclosure so the story stays short. The full raw form
/// of both lives in the Trace tab — leveling only decides what shows BY DEFAULT, never what exists. A source derives it
/// from its own record type. Serialized as its string name (like <see cref="TimelineSeverity"/>) so the wire is
/// forward-tolerant and round-trips for the SPA's string union.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineLevel
{
    Milestone,
    Detail,
}
