using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// Dry-run discovery of an agent pack in a bound repository — fetch + parse the agents under
/// <see cref="RootPath"/> (default "agents") at <see cref="Reference"/> into a preview. Persists nothing.
/// </summary>
public sealed record PreviewAgentPackQuery : IRequest<AgentPackPreview>, IRequireTeamMembership
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Branch / commit to read at; null = the repository's default branch.</summary>
    public string? Reference { get; init; }

    /// <summary>Directory to discover agents under; null = "agents".</summary>
    public string? RootPath { get; init; }
}
