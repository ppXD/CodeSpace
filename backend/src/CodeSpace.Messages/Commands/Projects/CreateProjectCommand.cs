using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Projects;

/// <summary>
/// Create a new project under the caller's team. <see cref="Slug"/> must match
/// <c>^[A-Za-z0-9_-]{1,64}$</c> (DB CHECK + app validator) and be unique-active per team.
/// Conflict on an active slug throws — operator either renames OR un-deletes the existing
/// row via the dedicated endpoint (TBD).
/// </summary>
public sealed record CreateProjectCommand : IRequest<Guid>, IRequireTeamMembership
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
