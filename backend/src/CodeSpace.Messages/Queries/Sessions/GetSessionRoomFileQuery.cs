using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// A generic preview of ONE file a turn produced, resolved from the producing agent's captured diff. Keyed by the
/// turn's run id + the repo-relative path (works for any run in the thread — a turn or an attempt). Team-scoped
/// (<see cref="IRequireTeamMembership"/>); a foreign / missing run is an indistinguishable not-found (null → 404).
/// </summary>
public sealed record GetSessionRoomFileQuery : IQuery<RoomFilePreview?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }

    public string Path { get; init; } = "";

    /// <summary>Optional: scope the preview to ONE specific agent's version of the file (per-agent attribution — open an agent, preview ITS file). Absent ⇒ the turn-wide newest-accepted-writer version.</summary>
    public Guid? AgentRunId { get; init; }
}
