using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks.Timeline;

/// <summary>
/// The ONLY closed axis of a timeline event — its render tone. Everything else (<c>Kind</c>) is an open string the
/// UI never switches on. A source derives the severity from its own record's outcome (e.g. node.failed → Error).
/// Serialized as its string name (like <c>PhaseStatus</c>) so the wire is forward-tolerant and round-trips for the
/// SPA's string union.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineSeverity
{
    Info,
    Success,
    Warning,
    Error,
}
