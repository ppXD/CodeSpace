using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>Admin qualification boundary. It observes runtime readiness but never changes profile state or consumers.</summary>
public interface IStorageProfileProbeService : IScopedDependency
{
    Task<StorageProfileProbeResult> ProbeAsync(StorageProfileProbeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One destination check. <paramref name="Initialize"/> marks a probe that is part of PROVISIONING — the only kind
/// allowed to create what is missing. A monitoring sweep must never set it: a probe that provisions cannot also
/// report on what was there.
/// </summary>
public sealed record StorageProfileProbeRequest(Guid TeamId, Guid ProfileId, int? ProfileRevision, bool VerifyWriteAccess, bool Initialize = false);
