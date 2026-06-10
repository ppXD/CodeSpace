namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// SIBLING capability to <see cref="IWorkspaceProvider"/> (Rule 7 / ISP — not a member widening on it):
/// reclaim workspaces orphaned by a crashed worker. The prepare→<see cref="IWorkspaceHandle.DisposeAsync"/>
/// lifecycle only removes the directory on the happy path (or on a prepare-failure), so a worker that
/// dies mid-run leaks its clone — which may still hold credentials if token-strip failed. A provider that
/// materialises persistent storage implements this so a periodic janitor can age the debris out; a
/// provider with no residue need not. Janitors are swept as a SET (the recurring job fans out over every
/// implementation), so a new storage backend's janitor is picked up with no wiring.
/// </summary>
public interface IWorkspaceJanitor
{
    /// <summary>The workspace family this janitor reclaims — matches the paired <see cref="IWorkspaceProvider.Kind"/>.</summary>
    string Kind { get; }

    /// <summary>
    /// Remove orphaned workspaces older than the implementation's staleness threshold. AGE-based on
    /// purpose: the threshold must exceed the maximum possible run duration, so the sweep can never touch
    /// a live run even when multiple workers share one storage root. Best-effort + idempotent + safe to
    /// run concurrently from multiple replicas. Returns the count reclaimed.
    /// </summary>
    Task<int> SweepStaleAsync(CancellationToken cancellationToken);
}
