using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Teams;

/// <summary>A newly created team, enough for the client to navigate to it.</summary>
public sealed record TeamSummary
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required TeamKind Kind { get; init; }
}
