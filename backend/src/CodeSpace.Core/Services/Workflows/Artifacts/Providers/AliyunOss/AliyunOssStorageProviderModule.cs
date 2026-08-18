using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Descriptor for Aliyun Object Storage Service. Installing it only makes the provider selectable in Settings: no
/// route, data class, or existing artifact path resolves to it until an operator creates a profile and points a route
/// at it, so adding this module cannot move a single byte that is written today.
/// </summary>
public sealed class AliyunOssStorageProviderModule : IStorageProviderModule
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
              "title": "Region",
              "description": "The region id without the oss- prefix, for example cn-hangzhou. It is scoped into every request signing key and must match the endpoint's region."
            },
            "bucket": {
              "type": "string",
              "minLength": 3,
              "maxLength": 63,
              "pattern": "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$",
              "title": "Bucket",
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
          "required": ["endpoint", "region", "bucket"],
          "additionalProperties": false
        }
        """);

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
        | StorageProviderCapabilities.HealthProbe;

    public Type FactoryType => typeof(AliyunOssArtifactStorageDriverFactory);

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
