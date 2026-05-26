using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Credentials;

public sealed record ListCredentialsQuery : IQuery<IReadOnlyList<CredentialSummary>>, IRequireTeamMembership
{
    public Guid? ProviderInstanceId { get; init; }
}
