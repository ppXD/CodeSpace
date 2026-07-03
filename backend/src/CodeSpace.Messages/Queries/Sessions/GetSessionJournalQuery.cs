using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// The backend-authored Session JOURNAL for a session, focused on <see cref="FocusRunId"/>'s turn when given (else the
/// latest). <see cref="Since"/> is the client's last-seen cursor for the <c>?since=</c> delta. Team-scoped
/// (<see cref="IRequireTeamMembership"/>); a foreign / missing session is an indistinguishable not-found (null → 404).
/// </summary>
public sealed record GetSessionJournalQuery : IQuery<JournalView?>, IRequireTeamMembership
{
    public Guid SessionId { get; init; }

    public Guid? FocusRunId { get; init; }

    /// <summary>The opaque cursor the client last saw — the delta anchor. Null / empty → the full journal.</summary>
    public string? Since { get; init; }
}
