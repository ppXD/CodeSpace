using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Teams;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Teams;

/// <summary>
/// Open a new workspace, owned by whoever asked.
///
/// <para>Gated on an INSTANCE capability rather than a team role, because there is no team yet to
/// hold a role — and deliberately gated at all: on a shared deployment, "anyone signed in may create
/// unlimited teams" is a resource decision, not an obvious default. Admins hold it by role; anyone
/// else is granted it individually, which is the whole reason this tier lives in data.</para>
///
/// <para>NOT team-scoped, so it carries no X-Team-Id and no membership check — the header would name
/// a team that has nothing to do with the request.</para>
/// </summary>
public sealed record CreateTeamCommand : ICommand<TeamSummary>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.TeamsCreate;

    public required string Name { get; init; }
}
