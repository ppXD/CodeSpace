namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// Resolves an <see cref="ITaskLaunchSeedProvider"/> by its open <see cref="ITaskLaunchSeedProvider.SurfaceKind"/>
/// string — same shape as <c>IAgentHarnessRegistry</c> / <c>ITaskProjectionRegistry</c>. The launch service picks
/// the surface from the request; this only maps surface → provider. A new surface becomes resolvable by registering
/// its provider — no edit here (the generic dispatch spine).
/// </summary>
public interface ITaskLaunchSeedProviderRegistry
{
    /// <summary>Every registered seed provider — for the "which launch surfaces are available" catalog (a later PR's surfaces endpoint).</summary>
    IReadOnlyList<ITaskLaunchSeedProvider> All { get; }

    /// <summary>Resolve the provider for <paramref name="surfaceKind"/>. Throws when none is registered for that surface.</summary>
    ITaskLaunchSeedProvider Resolve(string surfaceKind);

    /// <summary>Try to resolve the provider for <paramref name="surfaceKind"/>; returns false when none is registered.</summary>
    bool TryResolve(string surfaceKind, out ITaskLaunchSeedProvider provider);
}
