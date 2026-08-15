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

    /// <summary>Provider-native behaviours available to a future policy admission layer.</summary>
    StorageProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Concrete activation entry point for this module. The catalog records but never resolves or instantiates it;
    /// the dynamic profile/runtime slice will own that lifecycle.
    /// </summary>
    Type FactoryType { get; }
}
