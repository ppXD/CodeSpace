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
}
