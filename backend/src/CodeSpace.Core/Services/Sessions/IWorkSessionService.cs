using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Owns the <c>WorkSession</c> lifecycle (Rule 16 — the persistence + the turn-numbering live here, every caller
/// stays a thin dispatcher). The launch surfaces, the provider matcher, and the replay path all resolve a session
/// through this one seam, so none of them re-implements "which session + which turn"; they receive the pre-resolved
/// <see cref="SessionAssignment"/> the run starter stamps onto the run (the resolver-separation rule).
/// </summary>
public interface IWorkSessionService
{
    /// <summary>
    /// Open a NEW work session for a FIRST top-level turn and return the binding the run starter writes onto the
    /// run (<c>SessionId</c> + <c>SessionTurnIndex = 1</c>). The session row is STAGED onto the caller's unit of
    /// work (no <c>SaveChanges</c> here) — the ambient command transaction commits it atomically with the run, so a
    /// failed launch leaves no orphan session. <paramref name="title"/> is sanitised + truncated to the column
    /// width by the service (any caller may pass raw free text). <paramref name="kind"/> is the thread's product
    /// semantic (Task for a manual launch; a PR / Issue surface passes its own).
    /// </summary>
    Task<SessionAssignment> OpenAsync(Guid teamId, string title, WorkSessionKind kind, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Continue an EXISTING session with the NEXT top-level turn: validate it exists + belongs to
    /// <paramref name="teamId"/> (a foreign / missing session is an indistinguishable not-found — never a leak) and
    /// is still <c>Open</c>, then return a binding whose <c>TurnIndex</c> is one past the session's highest existing
    /// turn. Only top-level turns count — a child / replay run inherits the session with a NULL turn index and never
    /// bumps the ordinal (the "user-visible turns only" rule). Reads only; no row is written here.
    /// </summary>
    Task<SessionAssignment> ContinueAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Rename a session's thread title (team-scoped). Sanitises + truncates the title to the column width (the SAME
    /// transform as <see cref="OpenAsync"/>). Returns false when the session is foreign / missing (an indistinguishable
    /// not-found — never a leak), true when a row was renamed.
    /// </summary>
    Task<bool> RenameAsync(Guid sessionId, string title, Guid teamId, CancellationToken cancellationToken);
}
