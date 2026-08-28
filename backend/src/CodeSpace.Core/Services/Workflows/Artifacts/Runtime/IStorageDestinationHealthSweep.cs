using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Re-asks every destination a run currently depends on whether it still works.
///
/// <para>Without this the only probe anyone ever runs is the one an operator clicks, so a credential revoked at the
/// provider — the action the codebase itself tells operators to take — is invisible until a person opens an artifact
/// and the read fails. A destination's health is not a property of the moment it was configured.</para>
/// </summary>
public interface IStorageDestinationHealthSweep : IScopedDependency
{
    /// <summary>Probes every stale destination that an Active route currently binds writes to. Returns how many were probed.</summary>
    Task<int> ProbeStaleAsync(CancellationToken cancellationToken);
}
