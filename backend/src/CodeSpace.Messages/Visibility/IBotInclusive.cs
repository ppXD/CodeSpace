namespace CodeSpace.Messages.Visibility;

/// <summary>
/// Marker on a query/command that legitimately needs to SEE bot (non-human) users in its results —
/// e.g. resolving a message author's name when the author is the CodeSpace bot. The default for
/// every request is to EXCLUDE bots (an EF Core global query filter on <c>User</c>); implementing
/// this marker makes <c>BotVisibilityBehavior</c> flip the request-scoped <c>IBotVisibility</c> so
/// bots become visible for that request only. Opt-in, never opt-out — a new request can't leak bots
/// by forgetting a filter; it can only fail to see them until it explicitly asks.
/// </summary>
public interface IBotInclusive;
