using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// Dry-run discovery of an agent pack in a bound repository — fetch + parse the agents under
/// <see cref="RootPath"/> (default "agents") at <see cref="Reference"/> into a preview. Persists nothing.
/// <see cref="IRequireRepositoryAccess"/>, not bare membership: the files are read through the REPOSITORY's own
/// credential, so the id must be proven to belong to the caller's team or another team's private repository is read
/// with that team's token.
/// </summary>
public sealed record PreviewAgentPackQuery : IRequest<AgentPackPreview>, IRequireTeamMembership, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Branch / commit to read at; null = the repository's default branch.</summary>
    public string? Reference { get; init; }

    /// <summary>Directory to discover agents under; null = "agents".</summary>
    public string? RootPath { get; init; }
}
