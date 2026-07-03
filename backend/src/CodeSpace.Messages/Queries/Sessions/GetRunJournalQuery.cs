using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// The backend-authored Session JOURNAL (the chronological work transcript) for the session a run belongs to, focused
/// on that run's turn (and, for a prior attempt's run, that attempt's own flow). Works for any run in the thread.
/// <see cref="Since"/> is the client's last-seen cursor: when given, only the focused turn's steps AFTER it are returned
/// (the <c>?since=</c> delta), so a live poll re-sends only new steps. Team-scoped (<see cref="IRequireTeamMembership"/>);
/// a foreign / session-less / missing run is an indistinguishable not-found (null → 404).
/// </summary>
public sealed record GetRunJournalQuery : IQuery<JournalView?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }

    /// <summary>The opaque cursor the client last saw — the delta anchor. Null / empty → the full journal.</summary>
    public string? Since { get; init; }
}
