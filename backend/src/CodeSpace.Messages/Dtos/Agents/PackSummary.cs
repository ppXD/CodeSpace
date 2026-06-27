using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// A row in the Library/store's source list — one imported <c>Pack</c> (a github/git-url library) as a
/// category. Carries the source + freshness (<see cref="Reference"/> / <see cref="LastSyncedSha"/> /
/// <see cref="LastSyncedDate"/>) the UI shows on the pack header, plus the count of active agents + skills it
/// contributed so the rail can show "12 skills" without loading them.
/// </summary>
public sealed record PackSummary
{
    public required Guid Id { get; init; }
    public required PackKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? Reference { get; init; }
    public string? LastSyncedSha { get; init; }
    public DateTimeOffset? LastSyncedDate { get; init; }
    public required int AgentCount { get; init; }
    public required int SkillCount { get; init; }
}
