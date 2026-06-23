using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Enums;

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

    /// <summary>The opening turn of a brand-new session. A child / replay inherits the session with no new turn (a null index); only a top-level follow-up consumes the NEXT ordinal (a later slice).</summary>
    private const int FirstTurnIndex = 1;

    /// <summary>Fallback when the supplied title is blank after sanitisation — a session row always has a non-empty title.</summary>
    private const string DefaultTitle = "Untitled session";

    public WorkSessionService(CodeSpaceDbContext db)
    {
        _db = db;
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
            CreatedBy = actorUserId,
            LastModifiedBy = actorUserId,
        };

        _db.WorkSession.Add(session);

        return Task.FromResult(new SessionAssignment { SessionId = session.Id, TurnIndex = FirstTurnIndex });
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
