using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>
/// Derives the normalized <see cref="TaskLaunchSeed"/> for ONE launch surface (Rule 7 — a narrow,
/// single-responsibility contract). The registry resolves the right provider by its open
/// <see cref="SurfaceKind"/> string; the launch core never branches on the surface itself. A provider is the
/// ONLY thing that reads the surface payload (<c>TaskLaunchRequest.SurfacePayload</c>, the folded
/// <c>LaunchContext.Raw</c>) and erases its surface dimension into a uniform seed — so a NEW surface ships by
/// adding a provider in <c>Launch/Providers/&lt;Surface&gt;/</c> + a new surface-kind const, with ZERO edit to
/// the launch service / registry (Rule 18.3, the variant axis).
/// </summary>
public interface ITaskLaunchSeedProvider
{
    /// <summary>The launch surface this provider seeds (an open <see cref="TaskLaunchSurfaceKinds"/> string) — the registry indexes + resolves by it.</summary>
    string SurfaceKind { get; }

    /// <summary>Derive the normalized seed from <paramref name="request"/> (reading its surface payload as needed). Throws a clear validation error when the surface's own required input is missing (e.g. a blank chat goal).</summary>
    Task<TaskLaunchSeed> SeedAsync(TaskLaunchRequest request, CancellationToken cancellationToken);
}
