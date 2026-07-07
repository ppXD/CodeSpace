using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Projects a work session into its render-ready <see cref="JournalView"/> — the read side of the Session Journal
/// (the counterpart of the legacy room's projector, over the SAME session skeleton). Entered by session id or by any
/// run id; the FOCUSED turn is walked into its chronological steps, the rest are light cards. READ-ONLY, team-scoped
/// (a foreign / missing target → null, never a leak).
/// </summary>
public interface IJournalProjector
{
    /// <summary>The journal for the session a run belongs to, anchored at that run's turn. Null when the run is session-less / foreign / missing.</summary>
    Task<JournalView?> ProjectByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>The journal for a session, optionally focusing (walking) the turn a given run belongs to. Null when the session is missing / not this team's.</summary>
    Task<JournalView?> ProjectAsync(Guid sessionId, Guid? focusRunId, Guid teamId, CancellationToken cancellationToken);
}
