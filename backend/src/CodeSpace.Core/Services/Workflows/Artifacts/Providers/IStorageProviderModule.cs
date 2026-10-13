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
    /// Project the non-secret configuration onto the PHYSICAL namespace it reaches, spelled one way. The retention
    /// reaper re-derives it from stored revisions to decide whether one object key is one physical object across teams
    /// — unless the provider names that object itself (<see cref="IStorageProviderPhysicalObjects"/>), because its keys
    /// can leave the namespace its root names. Admission takes it too, to refuse a configuration that reaches nothing,
    /// but for one release still stores the hash of the configuration as written (<c>StorageProfileRules.StoredNamespaceFingerprint</c>).
    ///
    /// <para>So two configurations that reach the same namespace MUST project identically — a root with and without a
    /// trailing separator, a host and its alias — and where a provider cannot tell, it projects them together: a
    /// merged identity only keeps bytes, a split one lets one team's purge delete another's. A configuration that
    /// reaches no namespace at all throws. Operational tuning fields belong outside the projection. The default is the
    /// raw configuration, which is right only for a provider whose configuration has exactly one spelling; every
    /// shipped module overrides it.</para>
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
