using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Sessions;

/// <summary>
/// Rename a work session's human-facing thread title. Team-scoped via <see cref="IRequireTeamMembership"/> — the team
/// is resolved from <c>ICurrentTeam</c>, never the body; a foreign / missing session resolves to false (no existence
/// leak). The title is sanitised + truncated to the column width by the service. Returns true when a row was renamed.
/// </summary>
public sealed record RenameSessionCommand : ICommand<bool>, IRequireTeamMembership
{
    public Guid SessionId { get; init; }
    public string Title { get; init; } = "";
}
