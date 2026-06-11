using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Revoke a team model credential — soft-deletes it (Status=Revoked + DeletedDate) and clears the encrypted
/// key, so the just-in-time resolver treats it as unresolvable from that point. Team-scoped by the service.
/// </summary>
public sealed record RevokeModelCredentialCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid Id { get; init; }
}
