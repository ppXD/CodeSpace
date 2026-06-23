namespace CodeSpace.Messages.Enums;

/// <summary>
/// A <c>WorkSession</c>'s LIFECYCLE — and ONLY its lifecycle. It is never a run status: a session's
/// live execution state (running / needs-decision / failed) is PROJECTED from its runs + pending
/// decisions, NOT stored here. A thread is <see cref="Open"/> while it can still take new turns and
/// <see cref="Archived"/> once retired. Stored as the enum NAME via <c>HasConversion&lt;string&gt;</c>;
/// the literal values are pinned by <c>WorkSessionEnumTests</c>.
/// </summary>
public enum WorkSessionStatus
{
    /// <summary>Active thread — can still take new top-level turns.</summary>
    Open,

    /// <summary>Retired thread — kept for history, takes no new turns.</summary>
    Archived
}
