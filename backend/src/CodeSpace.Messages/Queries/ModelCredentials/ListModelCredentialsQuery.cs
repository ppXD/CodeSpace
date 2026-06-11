using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.ModelCredentials;

/// <summary>List the calling team's model credentials (secret-free summaries). Optionally filter to one provider.</summary>
public sealed record ListModelCredentialsQuery : IQuery<IReadOnlyList<ModelCredentialSummary>>, IRequireTeamMembership
{
    public string? Provider { get; init; }
}
