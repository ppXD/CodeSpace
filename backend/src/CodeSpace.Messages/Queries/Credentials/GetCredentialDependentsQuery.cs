using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

/// <summary>
/// What leans on a credential: the repositories bound through it (with their projects), the group hooks it registered,
/// the act-as identities it backs, and the workflow triggers that name those repositories. For a live credential it is
/// the Disconnect confirmation's "this will affect"; for a disconnected or retired one it is the list of what is waiting
/// for a new home.
///
/// <para>Membership alone, like the repository list it summarizes. Not keyed by the credential marker, which refuses a
/// soft-deleted credential — a departed member's retired token is the one a team most needs to ask about — so the
/// service loads it by (id, header team).</para>
/// </summary>
public sealed record GetCredentialDependentsQuery : IQuery<CredentialDependents>, IRequireTeamMembership
{
    public Guid CredentialId { get; init; }
}
