using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

public sealed record AddCredentialCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required Guid ProviderInstanceId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public required string DisplayName { get; init; }
    public required CredentialPayload Payload { get; init; }

    /// <summary>Defaults to Personal so existing callers (PAT / OAuth) are unchanged; the
    /// group-access-token endpoint sets TeamService.</summary>
    public CredentialOwnership Ownership { get; init; } = CredentialOwnership.Personal;
}
