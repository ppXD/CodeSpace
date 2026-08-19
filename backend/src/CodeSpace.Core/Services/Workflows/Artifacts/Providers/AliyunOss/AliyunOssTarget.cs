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

    private const string ServiceHostPrefix = "oss-";
    private const string ServiceDomainSuffix = ".aliyuncs.com";
    private const string VpcHostSuffix = "-internal";

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
            Region = ResolveRegion(configuration, endpoint.Host),
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
    /// The region every request is signed for: the profile's explicit <c>region</c> when it set one, otherwise the
    /// region its endpoint host names. An endpoint that names none is refused here rather than defaulted, because a
    /// wrong region does not fail visibly - it produces a signature OSS answers with SignatureDoesNotMatch, which the
    /// error contract classifies as Unauthorized and an operator reads as a credential fault they do not have.
    /// </summary>
    private static string ResolveRegion(JsonElement configuration, string endpointHost)
    {
        var configured = Read(configuration, "region");
        if (!string.IsNullOrEmpty(configured)) return configured;
        if (TryDeriveRegion(endpointHost, out var derived)) return derived;

        throw new ArgumentException($"Aliyun OSS endpoint '{endpointHost}' does not name a region, so the profile must set 'region' to the region its bucket lives in, without the oss- prefix, for example cn-hangzhou.", nameof(configuration));
    }

    /// <summary>
    /// Reads the region out of an OSS service host: <c>oss-{region}.aliyuncs.com</c> and the VPC form
    /// <c>oss-{region}-internal.aliyuncs.com</c>. Only those two shapes derive anything. A host outside the service
    /// domain - a custom domain or a CNAME - carries no region to read, and a service host whose label is a service
    /// name rather than a region id (<c>oss-accelerate</c>, <c>oss-accelerate-overseas</c>) must not hand that label
    /// over as one, so both fall to the explicit override instead.
    /// </summary>
    private static bool TryDeriveRegion(string endpointHost, out string region)
    {
        region = string.Empty;
        if (!endpointHost.StartsWith(ServiceHostPrefix, StringComparison.Ordinal) || !endpointHost.EndsWith(ServiceDomainSuffix, StringComparison.Ordinal)) return false;

        var label = endpointHost[ServiceHostPrefix.Length..^ServiceDomainSuffix.Length];
        if (label.EndsWith(VpcHostSuffix, StringComparison.Ordinal)) label = label[..^VpcHostSuffix.Length];
        if (!IsRegionId(label)) return false;

        region = label;
        return true;
    }

    /// <summary>
    /// An OSS region id is a two-letter area code plus at least one further segment: <c>cn-hangzhou</c>,
    /// <c>ap-southeast-1</c>, <c>cn-shanghai-finance-1</c>. Holding the derived label to that shape is what keeps
    /// <c>accelerate</c> and <c>accelerate-overseas</c> from being signed with as though they were regions, and - because
    /// a dot is not a character a segment may carry - what stops a host with an extra label between the <c>oss-</c>
    /// prefix and the service domain from yielding one either.
    /// </summary>
    private static bool IsRegionId(string label)
    {
        var segments = label.Split('-');
        if (segments.Length < 2 || segments[0].Length != 2 || !segments[0].All(char.IsAsciiLetter)) return false;

        return segments.Skip(1).All(segment => segment.Length != 0 && segment.All(char.IsAsciiLetterOrDigit));
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
