using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

public sealed record GetCredentialCapabilitiesQuery : IQuery<CredentialCapabilitiesResponse>, IRequireTeamMembership
{
    public required Guid CredentialId { get; init; }
}
