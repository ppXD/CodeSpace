using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Identity;

/// <summary>
/// Request-scoped switch that decides whether bot (non-human) users are visible to the EF Core
/// global query filter on <c>User</c>. Default <c>false</c> → bots are excluded from EVERY user
/// query (rosters, pickers, search, …) automatically, so a new query can never accidentally leak
/// the per-team CodeSpace bot. A request opts in by implementing <c>IBotInclusive</c> (the
/// <c>BotVisibilityBehavior</c> flips this), or a non-MediatR caller bypasses the filter directly
/// with <c>IgnoreQueryFilters()</c> (e.g. <c>ChatBotService</c> managing the bot itself).
/// </summary>
public interface IBotVisibility
{
    bool IncludeBots { get; set; }
}

public sealed class BotVisibility : IBotVisibility, IScopedDependency
{
    public bool IncludeBots { get; set; }
}
