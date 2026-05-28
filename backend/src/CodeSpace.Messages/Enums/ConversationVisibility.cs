namespace CodeSpace.Messages.Enums;

/// <summary>
/// Channel access model. Only meaningful for <see cref="ConversationKind.Channel"/> —
/// DM / group are implicitly private (their membership IS the access list).
/// </summary>
public enum ConversationVisibility
{
    /// <summary>In the channel directory; any team member may join + read.</summary>
    Public,

    /// <summary>Visible only to its members; invitation-gated.</summary>
    Private,
}
