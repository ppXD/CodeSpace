using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>Admin qualification boundary. It observes runtime readiness but never changes profile state or consumers.</summary>
public interface IStorageProfileProbeService : IScopedDependency
{
    Task<StorageProfileProbeResult> ProbeAsync(StorageProfileProbeRequest request, CancellationToken cancellationToken);
}

public sealed record StorageProfileProbeRequest(Guid TeamId, Guid ProfileId, int? ProfileRevision, bool VerifyWriteAccess);
