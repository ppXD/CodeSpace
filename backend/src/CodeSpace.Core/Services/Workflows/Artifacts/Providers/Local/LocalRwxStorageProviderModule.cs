using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

/// <summary>
/// Descriptor for the current shared-filesystem-compatible backend. Registering its inert factory does not create a
/// driver or activate it through <see cref="ArtifactStore"/>: that store continues to receive the convention-registered
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

    public string TypeKey => LocalRwxArtifactStorageDriverFactory.TypeKey;
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
