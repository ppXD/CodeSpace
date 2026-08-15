namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>Immutable lookup of storage driver factories validated against the provider modules installed in this build.</summary>
public interface IArtifactStorageDriverFactoryCatalog
{
    /// <summary>Returns null when the exact provider type/version has no factory in this build.</summary>
    IArtifactStorageDriverFactory? Get(string providerTypeKey);

    /// <summary>Returns the factory for the exact provider type/version or throws <see cref="NotSupportedException"/>.</summary>
    IArtifactStorageDriverFactory Require(string providerTypeKey);
}
