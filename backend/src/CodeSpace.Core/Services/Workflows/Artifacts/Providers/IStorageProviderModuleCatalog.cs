namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>Immutable runtime catalog of storage provider descriptors loaded into this build.</summary>
public interface IStorageProviderModuleCatalog
{
    IReadOnlyList<IStorageProviderModule> Modules { get; }

    /// <summary>Returns null when this build does not know the exact provider type/version.</summary>
    IStorageProviderModule? Get(string typeKey);

    /// <summary>Returns the exact provider type/version or throws <see cref="NotSupportedException"/>.</summary>
    IStorageProviderModule Require(string typeKey);
}
