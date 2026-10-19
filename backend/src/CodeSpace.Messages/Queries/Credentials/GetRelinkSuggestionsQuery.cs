using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

/// <summary>
/// The credentials the caller could move this credential's bindings onto, best first. For a credential that is gone: their
/// own live connections on the same provider instance — or on another connection of the team to the same host, after the same instance's —
/// (a connection made before this one was disconnected is flagged), then the team's service tokens; a teammate's personal token is never offered. For one still connected — the Disconnect
/// dialog asks "where should these go?" — exactly what a Disconnect that names the target accepts: another connection of
/// the same owner (for a team token, another team token), so an Admin asking about a teammate's credential is offered the
/// teammate's other connections, not their own. Like <see cref="GetCredentialDependentsQuery"/>, the credential may be
/// soft-deleted, so it is loaded by (id, header team) rather than through the credential marker.
/// </summary>
public sealed record GetRelinkSuggestionsQuery : IQuery<IReadOnlyList<RelinkSuggestion>>, IRequireTeamMembership
{
    public Guid CredentialId { get; init; }
}
