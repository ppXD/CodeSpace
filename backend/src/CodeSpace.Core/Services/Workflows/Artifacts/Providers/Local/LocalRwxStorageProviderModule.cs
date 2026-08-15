using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

/// <summary>
/// Descriptor for the current shared-filesystem-compatible backend. Merely publishing this descriptor does not
/// activate it through the new catalog: <see cref="ArtifactStore"/> continues to receive the convention-registered
/// <see cref="IArtifactBlobBackend"/> exactly as before until the profile/runtime cutover lands.
/// </summary>
public sealed class LocalRwxStorageProviderModule : IStorageProviderModule
{
    private static readonly JsonElement Config = ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "rootPath": {
              "type": "string",
              "minLength": 1,
              "title": "Root path",
              "description": "A durable filesystem path mounted read-write by every API and worker replica."
            }
          },
          "required": ["rootPath"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement Secrets = ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public string TypeKey => "local-rwx/v1";
    public string DisplayName => "Local / shared filesystem";
    public JsonElement ConfigSchema => Config;
    public JsonElement SecretSchema => Secrets;
    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.StreamingWrite
        | StorageProviderCapabilities.StreamingRead
        | StorageProviderCapabilities.RangeRead
        | StorageProviderCapabilities.ConditionalCreate
        | StorageProviderCapabilities.Delete
        | StorageProviderCapabilities.HealthProbe;
    public Type FactoryType => typeof(LocalRwxArtifactStorageDriverFactory);

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
