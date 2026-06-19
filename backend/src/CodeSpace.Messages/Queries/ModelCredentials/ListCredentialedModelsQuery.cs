using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.ModelCredentials;

/// <summary>List a credential's maintained models (the pick-from-list surface). Team-scoped to the caller; the credential must be one of the calling team's.</summary>
public sealed record ListCredentialedModelsQuery : IQuery<IReadOnlyList<CredentialedModelSummary>>, IRequireTeamMembership
{
    public Guid ModelCredentialId { get; init; }
}
