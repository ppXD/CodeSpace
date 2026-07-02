using CodeSpace.Messages.Dtos.Chat;

namespace CodeSpace.Core.Services.Chat;

/// <summary>
/// Conversation lifecycle + membership for the chat foundation. Handlers are thin Mediator
/// dispatchers (Rule 16); all business logic — DM singleton find-or-create, membership
/// gating, tenancy scoping, dm_key derivation — lives here.
///
/// <para>Tenant boundary: every method takes <c>teamId</c> (from <c>ICurrentTeam</c> via the
/// handler) and scopes every query by it. The MediatR pipeline already vetted membership; the
/// service still scopes defensively so a stolen conversation id can't read another team's row.</para>
/// </summary>
public interface IConversationService
{
    /// <summary>Create a public/private named channel + add the creator as Owner.</summary>
    Task<Guid> CreateChannelAsync(Guid teamId, string name, string slug, bool isPrivate, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// STAGE a channel + Owner membership onto the caller's unit of work WITHOUT saving — for composers whose
    /// whole graph must commit atomically (a task launch stages session + snapshot + run + this channel in one
    /// save, so a failed launch leaves no orphan channel). Same validation + slug normalization as
    /// <see cref="CreateChannelAsync"/> (which is now this + an immediate save).
    /// </summary>
    Task<Guid> StageChannelAsync(Guid teamId, string name, string slug, bool isPrivate, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Find-or-create the singleton DM between two users. Idempotent + race-safe via the
    /// dm_key unique index (migration 0029): concurrent opens collide and the loser re-queries
    /// the winner, so a pair NEVER ends up with two DM rows. Returns the same id on every call.
    /// </summary>
    Task<Guid> GetOrCreateDirectAsync(Guid teamId, Guid actorUserId, Guid otherUserId, CancellationToken cancellationToken);

    /// <summary>Create an ad-hoc group from a set of members (creator auto-included as Owner).</summary>
    Task<Guid> CreateGroupAsync(Guid teamId, string? name, IReadOnlyList<Guid> memberUserIds, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>Every conversation the user is an active member of, newest-first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListForUserAsync(Guid teamId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Single conversation — only if the user is an active member (else null, never leak existence).</summary>
    Task<ConversationSummary?> GetAsync(Guid teamId, Guid userId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Add a user to a channel / group. No-op if already an active member (idempotent).
    /// Throws when the actor isn't a member or the target conversation is a DM (DMs are fixed pairs).</summary>
    Task AddMemberAsync(Guid teamId, Guid actorUserId, Guid conversationId, Guid newMemberUserId, CancellationToken cancellationToken);
}
