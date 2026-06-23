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
}
