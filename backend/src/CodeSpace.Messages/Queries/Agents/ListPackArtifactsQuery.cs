using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using MediatR;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// One server-side page of a pack's STORE artifacts of a single kind, optionally filtered by name/handle — backs the
/// paginated Library detail tab and the New-agent / skill-binding pickers. <c>Page</c> is 0-based and clamped to the
/// available range server-side. Team scope comes from the request context (vetted by the membership pipeline).
/// </summary>
public sealed record ListPackArtifactsQuery : IRequest<PagedArtifacts>, IRequireTeamMembership
{
    public required Guid PackId { get; init; }
    public required PackArtifactKind Kind { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 20;
}
