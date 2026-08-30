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
///
/// <para>It is a request, not a guarantee: creating a destination is a write concern, so it is honoured only
/// alongside <paramref name="VerifyWriteAccess"/> — you may only create what you are about to prove you can write to.
/// A read-verified probe reports an absent destination as absent whatever it asked for.</para>
/// </summary>
public sealed record StorageProfileProbeRequest(Guid TeamId, Guid ProfileId, int? ProfileRevision, bool VerifyWriteAccess, bool Initialize = false);
