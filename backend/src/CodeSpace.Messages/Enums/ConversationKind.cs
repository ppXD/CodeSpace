namespace CodeSpace.Messages.Enums;

/// <summary>
/// What kind of conversation container this is. One model behind all three — the
/// message / membership / read-cursor machinery is identical; only addressing differs.
/// Persisted as a string (EF HasConversion&lt;string&gt;) so the column reads meaningfully
/// in the DB and a new kind doesn't shift integer ordinals.
/// </summary>
public enum ConversationKind
{
    /// <summary>1-on-1. Exactly two members, no name/slug.</summary>
    Direct,

    /// <summary>Ad-hoc group of 3+ members. Optional name, no slug.</summary>
    Group,

    /// <summary>Named, slugged, joinable. Public or private per <see cref="ConversationVisibility"/>.</summary>
    Channel,
}
