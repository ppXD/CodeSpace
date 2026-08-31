using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;

/// <summary>
/// Descriptor for the artifact bytes this deployment wrote BEFORE the CAS plane: content-addressed files under an
/// operator-mounted root, referenced by a <c>storage_url</c> and named by no <c>artifact_location</c> row at all —
/// which is why every monitoring component in the plane is blind to them.
///
/// <para>It is read-only by declaration, and permanently so. No <see cref="StorageProviderCapabilities.Delete"/>,
/// because these blobs are keyed by digest alone and therefore SHARED by every team that ever stored those bytes —
/// see <see cref="IStorageProviderTenantSharedObjectKeys"/>, whose marker this module carries and which the catalog
/// enforces against ever declaring Delete beside it. It carries <see cref="IStorageProviderAcceptsNoNewBytes"/> for
/// the other direction: the tier exists to be surveyed, streamed for sidecar adoption, and drained, never to receive
/// new bytes, so route binding refuses it outright instead of letting an operator discover that at the first artifact write.</para>
///
/// <para>It also declares no <see cref="IStorageProviderTeamNamespace"/>, so it cannot be a deployment default: one
/// root with no team segment is exactly the shared namespace that refusal exists for.</para>
/// </summary>
public sealed class LocalLegacyStorageProviderModule : IStorageProviderModule, IStorageProviderTenantSharedObjectKeys, IStorageProviderAcceptsNoNewBytes, IStorageProviderLegacyLayout
{
    private static readonly JsonElement Config = ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "rootPath": {
              "type": "string",
              "minLength": 1,
              "title": "Artifact store root",
              "description": "The directory the pre-CAS local blob backend wrote into, mounted read-only by every API and worker replica."
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

    public string TypeKey => LocalLegacyArtifactStorageDriverFactory.TypeKey;
    public string DisplayName => "Local filesystem (pre-CAS layout)";
    public JsonElement ConfigSchema => Config;
    public JsonElement SecretSchema => Secrets;
    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.HealthProbe | StorageProviderCapabilities.StreamingRead;
    public Type FactoryType => typeof(LocalLegacyArtifactStorageDriverFactory);

    public string? ResolveLegacyObjectKey(JsonElement nonSecretConfiguration, string sha256, string recordedLocator)
    {
        var key = LegacyLocalObjectKeys.For(sha256);
        if (key == null) return null;

        var rootPath = RootPath(nonSecretConfiguration);
        if (rootPath == null) return null;

        return LegacyLocalObjectKeys.NamesTheSameFile(rootPath, key, recordedLocator) ? key : null;
    }

    internal static string? RootPath(JsonElement nonSecretConfiguration)
    {
        if (nonSecretConfiguration.ValueKind != JsonValueKind.Object) return null;
        if (!nonSecretConfiguration.TryGetProperty("rootPath", out var root) || root.ValueKind != JsonValueKind.String) return null;

        var value = root.GetString();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
