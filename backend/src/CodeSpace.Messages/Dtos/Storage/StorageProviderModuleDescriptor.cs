using System.Text.Json;

namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>
/// Public discovery metadata for one storage provider type/version. It deliberately carries schemas rather than
/// profile values: secret inputs remain write-only, and the module's runtime factory is never part of the wire.
/// </summary>
public sealed record StorageProviderModuleDescriptor
{
    public required string TypeKey { get; init; }
    public required string DisplayName { get; init; }
    public required JsonElement ConfigSchema { get; init; }
    public required JsonElement SecretSchema { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// The <see cref="ConfigSchema"/> property that carries this provider's namespace, or null when it cannot
    /// subdivide one and therefore cannot be a deployment default at all.
    ///
    /// <para>An author of a DEPLOYMENT template must not set it — the server refuses a template config that does —
    /// because a template describes the whole deployment while that property names one team. A form has to know which
    /// field that is, or it can only find out by being rejected.</para>
    /// </summary>
    public string? TeamNamespaceProperty { get; init; }
}
