using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Re-asks the destinations a run currently depends on whether they still work — oldest observation first, a bounded
/// number per pass, so a growing population is covered over more passes rather than in one unbounded one.
///
/// <para>Without this the only probe anyone ever runs is the one an operator clicks, so a credential revoked at the
/// provider — the action the codebase itself tells operators to take — is invisible until a person opens an artifact
/// and the read fails. A destination's health is not a property of the moment it was configured.</para>
/// </summary>
public interface IStorageDestinationHealthSweep : IScopedDependency
{
    /// <summary>Probes the stale destinations an Active route binds writes to or that still hold stored objects — oldest observation first, bounded per pass. Returns how many were probed.</summary>
    Task<int> ProbeStaleAsync(CancellationToken cancellationToken);
}
