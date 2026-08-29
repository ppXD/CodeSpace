using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Descriptor for Aliyun Object Storage Service. Installing it only makes the provider selectable in Settings: no
/// route, data class, or existing artifact path resolves to it until an operator creates a profile and points a route
/// at it, so adding this module cannot move a single byte that is written today.
/// </summary>
public sealed class AliyunOssStorageProviderModule : IStorageProviderModule, IStorageProviderTeamNamespace
{
    internal static JsonElement ConfigSchemaDocument { get; } = ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "endpoint": {
              "type": "string",
              "minLength": 3,
              "maxLength": 253,
              "pattern": "^(https://)?[a-z0-9]([a-z0-9.-]*[a-z0-9])?$",
              "title": "Endpoint",
              "description": "The OSS service endpoint host, for example oss-cn-hangzhou.aliyuncs.com or its -internal VPC form. Always addressed over HTTPS."
            },
            "region": {
              "type": "string",
              "minLength": 2,
              "maxLength": 64,
              "pattern": "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$",
              "title": "Region override",
              "description": "Optional. The region id without the oss- prefix, for example cn-hangzhou. Leave it empty for an oss-{region}.aliyuncs.com endpoint or its -internal VPC form: the region is read from the endpoint host. Supply it for an endpoint that names no region - an accelerate endpoint, or a custom domain - because the region is scoped into every request signing key and a profile that cannot resolve one is refused when it is saved."
            },
            "bucket": {
              "type": "string",
              "minLength": 3,
              "maxLength": 63,
              "pattern": "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$",
              "title": "Bucket name",
              "description": "An existing bucket with versioning DISABLED. This driver never creates or deletes buckets. On a versioning-enabled bucket its staging discard leaves a delete marker and keeps the staged bytes as a non-current version, so every write would retain and bill a second full copy."
            },
            "keyPrefix": {
              "type": "string",
              "maxLength": 512,
              "pattern": "^([^/\\\\][^\\\\]*/)?$",
              "title": "Key prefix",
              "description": "Optional namespace inside the bucket, ending in a slash. Changing it is a new storage namespace, not a migration."
            }
          },
          "required": ["endpoint", "bucket"],
          "additionalProperties": false
        }
        """);

    /// <summary>The bucket is shared; the key prefix is what a team gets to itself.</summary>
    public string TeamNamespaceProperty => "keyPrefix";

    /// <summary>ConfigSchema requires a key prefix to end in a slash and to contain no backslash, so the join produces exactly that.</summary>
    public string ComposeTeamNamespace(string namespaceRoot, string teamSegment) => $"{namespaceRoot.Trim().Trim('/')}/{teamSegment}/";

    internal static JsonElement SecretSchemaDocument { get; } = ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "accessKeyId": {
              "type": "string",
              "minLength": 1,
              "maxLength": 128,
              "title": "AccessKey ID"
            },
            "accessKeySecret": {
              "type": "string",
              "minLength": 1,
              "maxLength": 256,
              "title": "AccessKey secret",
              "writeOnly": true
            },
            "securityToken": {
              "type": "string",
              "minLength": 1,
              "maxLength": 4096,
              "title": "STS security token",
              "description": "Only for temporary STS credentials. A profile using one stops working when the token expires.",
              "writeOnly": true
            }
          },
          "required": ["accessKeyId", "accessKeySecret"],
          "additionalProperties": false
        }
        """);

    public string TypeKey => AliyunOssArtifactStorageDriverFactory.TypeKey;
    public string DisplayName => "Aliyun OSS";
    public JsonElement ConfigSchema => ConfigSchemaDocument;
    public JsonElement SecretSchema => SecretSchemaDocument;

    /// <summary>
    /// Every declared capability is one the driver implements over real OSS verbs. Multipart upload, object versioning,
    /// signed download, and server-side encryption are deliberately absent: they are either unimplemented here or a
    /// bucket-level operator setting this module cannot promise on the profile's behalf.
    /// </summary>
    public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.StreamingWrite
        | StorageProviderCapabilities.StreamingRead
        | StorageProviderCapabilities.RangeRead
        | StorageProviderCapabilities.ConditionalCreate
        | StorageProviderCapabilities.Delete
        | StorageProviderCapabilities.HealthProbe
        | StorageProviderCapabilities.StableETag;

    public Type FactoryType => typeof(AliyunOssArtifactStorageDriverFactory);

    /// <summary>
    /// The schema admits an endpoint as a host pattern, but the signing region is read out of that host and a host
    /// naming none is configurable only with an explicit <c>region</c>. Leaving that to activation would let Settings
    /// store a profile whose first artifact write fails inside a run, so the control plane runs the same parser the
    /// factory activates with - one implementation, so admission and activation cannot answer differently.
    /// </summary>
    public void EnsureConfigurationReadable(JsonElement nonSecretConfiguration) => AliyunOssTarget.Parse(nonSecretConfiguration);

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
