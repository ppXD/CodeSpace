using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="IWorkSessionService"/>. Stages a <c>work_session</c> row onto the shared, request-scoped
/// <see cref="CodeSpaceDbContext"/> and returns the binding for the run starter — the session is flushed by the
/// SAME unit of work that commits the run (the ambient <c>TransactionalBehavior</c> in production, the run
/// starter's own <c>SaveChanges</c> on a direct call), so the two rows land atomically.
/// </summary>
public sealed class WorkSessionService : IWorkSessionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly Chat.IConversationService _conversations;

    /// <summary>The opening turn of a brand-new session. A child / replay inherits the session with no new turn (a null index); only a top-level follow-up consumes the NEXT ordinal (a later slice).</summary>
    private const int FirstTurnIndex = 1;

    /// <summary>Fallback when the supplied title is blank after sanitisation — a session row always has a non-empty title.</summary>
    private const string DefaultTitle = "Untitled session";

    public WorkSessionService(CodeSpaceDbContext db, Chat.IConversationService conversations)
    {
        _db = db;
        _conversations = conversations;
    }

    public Task<SessionAssignment> OpenAsync(Guid teamId, string title, WorkSessionKind kind, Guid actorUserId, CancellationToken cancellationToken)
    {
        // uuid v7 → time-sortable id, so "a team's sessions, newest first" can order by id when needed (same stance as Message ids).
        var session = new WorkSession
        {
            Id = Guid.CreateVersion7(),
            TeamId = teamId,
            Title = SanitizeTitle(title),
            Kind = kind,
            Status = WorkSessionStatus.Open,
            LastActivityAt = DateTimeOffset.UtcNow,   // opening activity — the MRU sort key; bumped on every continue below
            CreatedBy = actorUserId,
            LastModifiedBy = actorUserId,
        };

        _db.WorkSession.Add(session);

        return Task.FromResult(new SessionAssignment { SessionId = session.Id, TurnIndex = FirstTurnIndex });
    }

    public async Task<SessionAssignment> ContinueAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        // Atomically CLAIM the next turn ordinal: the UPDATE … RETURNING row-locks the session, so two CONCURRENT
        // continues to the same session SERIALISE here and get DISTINCT ordinals — no MAX+1 read-committed race, no
        // duplicate top-level turn by construction. Team- + Open-scoped, so a foreign / archived session claims nothing
        // (0 rows). A child / replay inherits the SessionId with a NULL turn index and never reaches here, so it can't
        // bump the counter. The claim runs in the launch's transaction, so a failed launch rolls the increment back.
        var claimed = await _db.Database
            .SqlQueryRaw<int>(
                "UPDATE work_session SET last_turn_index = last_turn_index + 1, last_activity_at = now() WHERE id = {0} AND team_id = {1} AND status = 'Open' RETURNING last_turn_index AS \"Value\"",
                sessionId, teamId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (claimed.Count == 1) return new SessionAssignment { SessionId = sessionId, TurnIndex = claimed[0] };

        // 0 rows — the claim matched no OPEN, team-owned session. A best-effort lookup distinguishes a foreign / missing
        // session (KeyNotFound, no existence leak — a cross-team session reads the SAME not-found) from an archived one
        // (InvalidOperation); the failed claim above is the authority, this only shapes the error.
        var status = await _db.WorkSession.AsNoTracking()
            .Where(s => s.Id == sessionId && s.TeamId == teamId)
            .Select(s => (WorkSessionStatus?)s.Status)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (status is { } st)
            throw new InvalidOperationException($"Session {sessionId} is {st} and cannot take a new turn.");

        throw new KeyNotFoundException($"Session {sessionId} not found or not accessible.");
    }

    public async Task<Guid> EnsureConversationAsync(Guid sessionId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var session = await FindOwnedSessionAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        if (await AliveLinkedConversationIdAsync(session, cancellationToken).ConfigureAwait(false) is { } linked)
        {
            await EnsureMemberStagedAsync(linked, teamId, actorUserId, cancellationToken).ConfigureAwait(false);

            return linked;
        }

        var slug = SessionChannelSlug(sessionId);

        // Adopt-by-slug makes a concurrent / crashed first-time ensure CONVERGENT: the deterministic slug is the
        // idempotency key, so a racer that lost the link still finds ONE channel rather than minting a twin.
        var conversationId = await AliveChannelIdBySlugAsync(teamId, slug, cancellationToken).ConfigureAwait(false)
            ?? await _conversations.StageChannelAsync(teamId, SessionChannelName(session.Title), slug, isPrivate: false, actorUserId, cancellationToken).ConfigureAwait(false);

        // A DIFFERENT team member continuing the thread must still see the room its cards post into — the staging
        // path adds only the CREATOR as Owner, so the linked/adopted paths upsert the current actor as a member.
        await EnsureMemberStagedAsync(conversationId, teamId, actorUserId, cancellationToken).ConfigureAwait(false);

        session.ConversationId = conversationId;

        return conversationId;
    }

    /// <summary>Stage a membership for the actor when none exists — tracked-first (the channel may be freshly STAGED in this same unit of work), then the DB. Idempotent within the scope.</summary>
    private async Task EnsureMemberStagedAsync(Guid conversationId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (_db.ConversationMember.Local.Any(m => m.ConversationId == conversationId && m.UserId == actorUserId)) return;

        if (await _db.ConversationMember.AsNoTracking().AnyAsync(m => m.ConversationId == conversationId && m.UserId == actorUserId, cancellationToken).ConfigureAwait(false)) return;

        _db.ConversationMember.Add(new ConversationMember { ConversationId = conversationId, TeamId = teamId, UserId = actorUserId, Role = ConversationMemberRole.Member, JoinedDate = DateTimeOffset.UtcNow });
    }

    /// <summary>The tracked session row (a just-STAGED fresh open or the persisted continue target) — <c>FindAsync</c> reads the change tracker first, so the fresh-launch path sees its own uncommitted session. Team-scoped fail-closed.</summary>
    private async Task<WorkSession> FindOwnedSessionAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        var session = await _db.WorkSession.FindAsync(new object[] { sessionId }, cancellationToken).ConfigureAwait(false);

        if (session == null || session.TeamId != teamId)
            throw new KeyNotFoundException($"Session {sessionId} not found.");

        return session;
    }

    /// <summary>The session's linked channel id when the link is set AND the channel is still alive in this team — a deleted / foreign channel reads as unlinked, so the ensure re-creates instead of parking cards into a dead room.</summary>
    private async Task<Guid?> AliveLinkedConversationIdAsync(WorkSession session, CancellationToken cancellationToken)
    {
        if (session.ConversationId is not { } linked) return null;

        var alive = await _db.Conversation.AsNoTracking()
            .AnyAsync(c => c.Id == linked && c.TeamId == session.TeamId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        return alive ? linked : null;
    }

    private async Task<Guid?> AliveChannelIdBySlugAsync(Guid teamId, string slug, CancellationToken cancellationToken)
    {
        // Tracked-first: a channel STAGED earlier in this same unit of work (a second ensure before the commit —
        // e.g. the S6 chat-with-agent caller) is invisible to a DB read; missing it would stage a slug twin that
        // dies on the unique index at commit.
        if (_db.Conversation.Local.FirstOrDefault(c => c.TeamId == teamId && c.Slug == slug && c.DeletedDate == null) is { } staged) return staged.Id;

        return await _db.Conversation.AsNoTracking()
            .Where(c => c.TeamId == teamId && c.Slug == slug && c.DeletedDate == null)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The session's deterministic channel slug — the idempotency key adopt-by-slug converges on.</summary>
    private static string SessionChannelSlug(Guid sessionId) => $"task-{sessionId:N}"[..17];

    /// <summary>The channel's display name — the thread title, clipped to a chat-friendly width (surrogate-safe: never cuts an emoji mid-pair).</summary>
    internal static string SessionChannelName(string title)
    {
        if (title.Length <= 60) return title;

        var cut = char.IsHighSurrogate(title[58]) ? 58 : 59;

        return title[..cut] + "…";
    }

    public async Task<bool> RenameAsync(Guid sessionId, string title, Guid teamId, CancellationToken cancellationToken)
    {
        var session = await _db.WorkSession
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (session == null) return false;   // foreign / missing — indistinguishable not-found, never a leak

        session.Title = SanitizeTitle(title);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Make any free-text goal a safe one-line session title: collapse every run of whitespace (incl. newlines /
    /// tabs) to a single space + trim, fall back to <see cref="DefaultTitle"/> when nothing is left, and truncate to
    /// <see cref="WorkSession.TitleMaxLength"/> (with an ellipsis) so the title can never overflow the column.
    /// Internal (not private) so the pure transform is unit-pinned directly via InternalsVisibleTo.
    /// </summary>
    internal static string SanitizeTitle(string raw)
    {
        var collapsed = string.Join(' ', (raw ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length == 0) return DefaultTitle;

        if (collapsed.Length <= WorkSession.TitleMaxLength) return collapsed;

        // Truncate to (max - 1) chars + an ellipsis. Back off one more char if the cut would split an astral
        // surrogate PAIR (e.g. an emoji), so we never emit a lone surrogate — the result is still <= max chars.
        var cut = WorkSession.TitleMaxLength - 1;
        if (char.IsHighSurrogate(collapsed[cut - 1])) cut--;

        return collapsed[..cut] + '…';
    }
}
