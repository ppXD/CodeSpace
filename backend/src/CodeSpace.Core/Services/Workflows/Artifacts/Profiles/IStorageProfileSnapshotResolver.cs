using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>
/// Provider-neutral, scoped read boundary that resolves one exact persisted profile revision into an immutable
/// snapshot. Readiness failures are values; implementations must not activate a factory or resolve secret material.
/// </summary>
public interface IStorageProfileSnapshotResolver : IScopedDependency
{
    Task<StorageProfileSnapshotResolution> ResolveAsync(StorageProfileSnapshotRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// An explicit team/profile/revision pin plus what the caller wants the profile for. There is deliberately no implicit
/// "current" sentinel and no implicit eligibility: <see cref="StorageProfileEligibility"/> is stated at every call site.
/// </summary>
public sealed record StorageProfileSnapshotRequest(Guid TeamId, Guid ProfileId, int ProfileRevision, StorageProfileEligibility Eligibility);

/// <summary>Closed result vocabulary for expected runtime readiness outcomes.</summary>
public abstract record StorageProfileSnapshotResolution
{
    private StorageProfileSnapshotResolution() { }

    public sealed record Ready(StorageProfileSnapshot Snapshot) : StorageProfileSnapshotResolution;
    public sealed record Missing : StorageProfileSnapshotResolution;
    public sealed record NotActive(StorageProfileState State) : StorageProfileSnapshotResolution;
    public sealed record RevisionMissing : StorageProfileSnapshotResolution;
    public sealed record ProviderUnavailable(string ProviderTypeKey, StorageProfileProviderUnavailableReason Reason) : StorageProfileSnapshotResolution;
    public sealed record Invalid(StorageProfileSnapshotInvalidReason Reason) : StorageProfileSnapshotResolution;
    public sealed record CredentialUnavailable(StorageProfileCredentialUnavailableReason Reason) : StorageProfileSnapshotResolution;
    public sealed record CredentialInvalid(StorageProfileCredentialInvalidReason Reason) : StorageProfileSnapshotResolution;
}

public enum StorageProfileProviderUnavailableReason
{
    ModuleMissing,
    FactoryMissing,
}

public enum StorageProfileSnapshotInvalidReason
{
    Configuration,
}

public enum StorageProfileCredentialUnavailableReason
{
    Missing,
    NotActive,
    RevisionMissing,
}

public enum StorageProfileCredentialInvalidReason
{
    MalformedReference,
    ProviderMismatch,
}
