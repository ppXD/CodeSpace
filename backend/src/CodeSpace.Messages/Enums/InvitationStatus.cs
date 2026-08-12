namespace CodeSpace.Messages.Enums;

/// <summary>
/// Terminal in two of three states. An invitation is spent the moment it is accepted and dead the
/// moment it is revoked; neither returns to Pending, which is what makes a token single-use.
/// Expiry is NOT a status — it is a timestamp, so a link cannot be revived by a clock change and no
/// sweep has to run for expiry to take effect.
/// </summary>
public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
}
