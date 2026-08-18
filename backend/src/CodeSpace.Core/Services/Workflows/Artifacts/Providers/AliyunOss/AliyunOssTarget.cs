using System.Text;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The addressable half of a profile: which bucket, over which endpoint, signed for which region. OSS is addressed
/// virtual-hosted (<c>https://{bucket}.{endpoint}/{key}</c>) while the V4 canonical resource stays <c>/{bucket}/{key}</c>,
/// so the two forms are built here rather than at each call site.
/// </summary>
internal sealed record AliyunOssTarget
{
    /// <summary>OSS caps an object key at 1023 UTF-8 bytes; the prefix and the reserved segment count against it.</summary>
    public const int MaxKeyBytes = 1023;

    public required Uri Endpoint { get; init; }
    public required string Region { get; init; }
    public required string Bucket { get; init; }
    public required string KeyPrefix { get; init; }

    public static AliyunOssTarget Parse(JsonElement configuration)
    {
        StorageProviderJson.Validate(configuration, AliyunOssStorageProviderModule.ConfigSchemaDocument, "Aliyun OSS profile configuration", "ConfigSchema");

        var host = Read(configuration, "endpoint")!;
        var bucket = Read(configuration, "bucket")!;
        if (!Uri.TryCreate(Absolute(host), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Aliyun OSS profile configuration requires an HTTPS endpoint host.", nameof(configuration));

        var keyPrefix = Read(configuration, "keyPrefix") ?? string.Empty;
        if (!IsAddressablePrefix(keyPrefix))
            throw new ArgumentException("Aliyun OSS profile configuration requires a keyPrefix of non-empty, non-traversal segments ending in '/'.", nameof(configuration));

        return new AliyunOssTarget
        {
            Endpoint = new Uri($"https://{bucket}.{endpoint.Host}", UriKind.Absolute),
            Region = Read(configuration, "region")!,
            Bucket = bucket,
            KeyPrefix = keyPrefix
        };
    }

    public string ResourcePath(string ossKey) => $"/{Bucket}/{ossKey}";

    public Uri ObjectUri(string ossKey, string query = "") => new($"{Endpoint.GetLeftPart(UriPartial.Authority)}/{AliyunOssV4Signer.Encode(ossKey, escapeSlash: false)}{query}", UriKind.Absolute);

    /// <summary>
    /// Projects a caller's object key onto a bucket key, refusing anything that could escape the profile's prefix.
    /// Traversal and empty segments are rejected rather than normalized so two different caller keys can never
    /// collapse onto one stored object.
    /// </summary>
    public bool TryResolveKey(string objectKey, string area, out string ossKey)
    {
        ossKey = string.Empty;
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.IndexOf('\0') >= 0 || Path.IsPathRooted(objectKey)) return false;

        var segments = objectKey.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (!SegmentsAreAddressable(segments)) return false;

        var candidate = KeyPrefix + area + string.Join('/', segments);
        if (Encoding.UTF8.GetByteCount(candidate) > MaxKeyBytes) return false;

        ossKey = candidate;
        return true;
    }

    /// <summary>
    /// The profile's own half of every key, held to the same segment rule as a caller's. A dot segment here would pass
    /// the schema pattern and then desynchronize the two path forms: <see cref="ObjectUri"/> lets Uri compress it out
    /// of the request path while the V4 signature covers the literal <see cref="ResourcePath"/>, so every request from
    /// such a profile would 403. Refusing it at parse time turns a permanently dead profile into an activation error.
    /// </summary>
    private static bool IsAddressablePrefix(string keyPrefix) =>
        keyPrefix.Length == 0 || (keyPrefix.EndsWith('/') && SegmentsAreAddressable(keyPrefix[..^1].Split('/')));

    /// <summary>The one segment rule, shared by both halves of a bucket key so the two can never disagree.</summary>
    private static bool SegmentsAreAddressable(IEnumerable<string> segments) =>
        segments.All(segment => segment.Length != 0 && segment is not ("." or "..") && !segment.Any(char.IsControl));

    private static string Absolute(string host) => host.StartsWith("https://", StringComparison.Ordinal) ? host : "https://" + host;

    private static string? Read(JsonElement configuration, string name) =>
        configuration.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
