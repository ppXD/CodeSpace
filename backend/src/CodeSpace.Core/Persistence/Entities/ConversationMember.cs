using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Membership of a user in a <see cref="Conversation"/> — works uniformly for DM (2 rows),
/// group (N rows), and channel (rows for joined members). Carries the per-member read
/// cursor so unread counts are a single indexed range query (messages with id greater than
/// <see cref="LastReadMessageId"/>) rather than a per-message read-receipt table that would
/// explode to members × messages.
///
/// <para>Composite PK (<see cref="ConversationId"/>, <see cref="UserId"/>) — same link-table
/// shape as <see cref="ProjectRepository"/>. Soft-deleted so a leave / re-join cycle keeps
/// the audit trail.</para>
/// </summary>
public class ConversationMember : IAuditable
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Denormalised team — same as <c>Conversation.TeamId</c>. Lets "my conversations"
    /// and tenancy-filtered queries index without joining through conversation.</summary>
    public Guid TeamId { get; set; }

    public ConversationMemberRole Role { get; set; } = ConversationMemberRole.Member;

    /// <summary>
    /// Read cursor: the id (UUID v7, time-sortable) of the last message this user has seen.
    /// Unread = messages in the conversation with <c>id > LastReadMessageId</c>. Null until
    /// the user has read anything (whole conversation is unread).
    /// </summary>
    public Guid? LastReadMessageId { get; set; }

    /// <summary>Suppresses unread badges / notifications without leaving the conversation.</summary>
    public bool Muted { get; set; }

    public DateTimeOffset JoinedDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Conversation Conversation { get; set; } = default!;
}
