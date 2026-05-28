using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Unified conversation container — the single model behind every chat surface:
/// 1-on-1 DMs, ad-hoc group chats, and named channels. Slack / Discord model it the
/// same way (one row + a kind discriminator) rather than separate tables per surface,
/// because the message / membership / read-cursor machinery is identical across all
/// three — only the addressing + naming differ.
///
/// <list type="bullet">
///   <item><see cref="ConversationKind.Direct"/> — exactly two members, no name/slug.
///         Uniqueness of the pair is enforced in the service layer (find-or-create).</item>
///   <item><see cref="ConversationKind.Group"/> — 3+ ad-hoc members, optional name, no slug.</item>
///   <item><see cref="ConversationKind.Channel"/> — named + slugged, <see cref="Visibility"/>
///         public (any team member can join/see) or private (membership-gated).</item>
/// </list>
///
/// <para>Tenancy: <see cref="TeamId"/> scopes every conversation to one team. Cross-team
/// conversations don't exist — a DM spanning two teams would be a tenancy leak.</para>
/// </summary>
public class Conversation : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    public ConversationKind Kind { get; set; }

    /// <summary>
    /// URL-safe handle, unique per team. Channels only — null for DM / group. Drives the
    /// addressable <c>#channel-name</c> reference + the channel-directory route.
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>Display name. Channels + optionally groups. Null for DM (rendered from the
    /// other member's name at read time).</summary>
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Direct-message singleton key: <c>{minUserId}:{maxUserId}</c> (sorted, order-independent),
    /// set only for <see cref="ConversationKind.Direct"/>. Null for channel / group. The partial
    /// unique index on (team_id, dm_key) makes find-or-create-DM race-safe — see migration 0029.
    /// </summary>
    public string? DmKey { get; set; }

    /// <summary>
    /// Only meaningful for <see cref="ConversationKind.Channel"/>. Public channels appear in
    /// the directory and any team member may join; private channels are visible only to their
    /// members. DM / group are implicitly private (their membership IS the access list).
    /// </summary>
    public ConversationVisibility Visibility { get; set; } = ConversationVisibility.Public;

    public bool Archived { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }

    public Team Team { get; set; } = default!;
}
