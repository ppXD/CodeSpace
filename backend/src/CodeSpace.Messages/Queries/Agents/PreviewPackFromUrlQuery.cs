using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// Dry-run discovery of a pack at a git URL — clone (host-allowlist-guarded) + recursively discover the agents
/// AND skills + per-team conflict check into a <see cref="PackPreview"/>. Persists nothing; the transient clone
/// is reclaimed. The URL-driven, multi-format successor to <see cref="PreviewAgentPackQuery"/>.
/// </summary>
public sealed record PreviewPackFromUrlQuery : IRequest<PackPreview>, IRequireTeamMembership
{
    /// <summary>The public git URL to clone (an https github.com / gitlab.com URL, or an operator-allowlisted host).</summary>
    public required string Url { get; init; }

    /// <summary>Branch / tag to read at; null = the repository's default branch.</summary>
    public string? Reference { get; init; }
}
