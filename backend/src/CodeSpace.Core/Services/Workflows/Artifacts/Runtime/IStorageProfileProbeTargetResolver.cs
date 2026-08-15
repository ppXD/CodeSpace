using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>Minimal team-scoped projection used to pin current-or-requested profile identity before activation.</summary>
public interface IStorageProfileProbeTargetResolver : IScopedDependency
{
    Task<StorageProfileProbeTarget?> ResolveAsync(StorageProfileProbeTargetRequest request, CancellationToken cancellationToken);
}

public sealed record StorageProfileProbeTargetRequest(Guid TeamId, Guid ProfileId, int? ProfileRevision);
public sealed record StorageProfileProbeTarget(Guid ProfileId, int ProfileRevision, string? ProviderTypeKey);
