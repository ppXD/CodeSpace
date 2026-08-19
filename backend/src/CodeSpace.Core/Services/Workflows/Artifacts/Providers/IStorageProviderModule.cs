using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// Pure descriptor for one storage provider TYPE. Profiles and credentials are deliberately absent: a later slice
/// persists those operator-created instances, while this catalog answers only which provider implementations this
/// build knows how to activate and which schema a Settings UI should render.
/// </summary>
public interface IStorageProviderModule
{
    /// <summary>Open, canonical, major-versioned wire key, for example <c>aliyun-oss/v1</c>.</summary>
    string TypeKey { get; }

    /// <summary>Operator-facing provider name.</summary>
    string DisplayName { get; }

    /// <summary>JSON Schema for non-secret profile configuration. The schema document itself must be an object.</summary>
    JsonElement ConfigSchema { get; }

    /// <summary>JSON Schema for write-only secret inputs. Secret values never belong in <see cref="ConfigSchema"/>.</summary>
    JsonElement SecretSchema { get; }

    /// <summary>
    /// Project the non-secret configuration fields that identify the provider namespace. The control plane canonicalizes
    /// and hashes this server-owned projection; clients can never submit a fingerprint. Providers with operational
    /// tuning fields should override the conservative default so those fields do not create a new namespace identity.
    /// </summary>
    JsonElement GetNamespaceConfiguration(JsonElement nonSecretConfiguration) => nonSecretConfiguration.Clone();

    /// <summary>
    /// Raise the refusal this module's own activation path would raise for a configuration <see cref="ConfigSchema"/>
    /// already admits. The control plane calls it while admitting a profile revision, so a configuration the provider
    /// cannot read is refused as the revision is saved instead of at the first artifact write. It proves only that
    /// the provider can READ the configuration - never that the credential, the network, or the bucket is usable - so a
    /// module whose schema is its whole readability rule keeps this no-op default.
    /// </summary>
    void EnsureConfigurationReadable(JsonElement nonSecretConfiguration) { }

    /// <summary>Provider-native behaviours available to a future policy admission layer.</summary>
    StorageProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Concrete activation entry point for this module. Startup validates an exact registered factory instance against
    /// this type, but the dynamic profile/runtime slice will own calling it and the resulting driver lifecycle.
    /// </summary>
    Type FactoryType { get; }
}
