using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

/// <summary>
/// Descriptor for the current shared-filesystem-compatible backend. Registering its inert factory does not create a
/// driver or activate it through <see cref="ArtifactStore"/>: that store continues to receive the convention-registered
/// <see cref="IArtifactBlobBackend"/> exactly as before until the profile/runtime cutover lands.
/// </summary>
public sealed class LocalRwxStorageProviderModule : IStorageProviderModule, IStorageProviderTeamNamespace, IStorageProviderPhysicalObjects
{
    /// <summary>
    /// The directory beneath the root that <see cref="LocalRwxArtifactStorageDriver"/> keeps every object in
    /// (<c>TryResolveObjectPath</c>); restated here, and pinned against the real driver by a unit test.
    /// </summary>
    private const string ObjectsDirectory = "objects";

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

    /// <summary>The mount is shared; the directory beneath it is what a team gets to itself.</summary>
    public string TeamNamespaceProperty => "rootPath";

    /// <summary>A filesystem path, so the join is a directory beneath the root and carries no trailing separator.</summary>
    public string ComposeTeamNamespace(string namespaceRoot, string teamSegment) => $"{namespaceRoot.Trim().TrimEnd('/')}/{teamSegment}";

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

    /// <summary>The directory the root reaches, not the text that names it: see <see cref="LocalRootPath"/>.</summary>
    public JsonElement GetNamespaceConfiguration(JsonElement nonSecretConfiguration) => LocalRootPath.NamespaceConfiguration(nonSecretConfiguration);

    /// <summary>The file the driver's delete of that key would unlink, every link on the way to it followed: see <see cref="LocalRootPath.ObjectPath"/>.</summary>
    public string PhysicalObject(JsonElement nonSecretConfiguration, string objectKey) => LocalRootPath.ObjectPath(nonSecretConfiguration, ObjectsDirectory, objectKey);

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
