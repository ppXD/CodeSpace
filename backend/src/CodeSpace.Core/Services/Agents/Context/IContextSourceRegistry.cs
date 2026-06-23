namespace CodeSpace.Core.Services.Agents.Context;

/// <summary>
/// Resolves an <see cref="IContextSource"/> by its open <see cref="IContextSource.Kind"/> string — same shape as
/// <c>ITaskLaunchSeedProviderRegistry</c> / <c>IAgentHarnessRegistry</c>. The <c>get_context</c> tool reads
/// <see cref="All"/> to advertise the available sources and dispatches by <see cref="TryResolve"/>; a new source
/// becomes retrievable by registering its impl — no edit here (the generic dispatch spine).
/// </summary>
public interface IContextSourceRegistry
{
    /// <summary>Every registered source — for the tool's "which context can I pull" catalog (and the "unknown source" hint).</summary>
    IReadOnlyList<IContextSource> All { get; }

    /// <summary>Resolve the source for <paramref name="kind"/>. Throws when none is registered for that kind.</summary>
    IContextSource Resolve(string kind);

    /// <summary>Try to resolve the source for <paramref name="kind"/>; returns false when none is registered.</summary>
    bool TryResolve(string kind, out IContextSource source);
}
