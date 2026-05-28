namespace CodeSpace.Messages.Enums;

/// <summary>
/// A member's role within one conversation. Owners can rename / archive / manage
/// membership of a channel; members participate. DM members are both implicitly owners
/// in practice but stored as Member — there's nothing to administer on a DM.
/// </summary>
public enum ConversationMemberRole
{
    Member,
    Owner,
}
