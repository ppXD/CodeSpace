using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Aliyun OSS signature version 4 (<c>OSS4-HMAC-SHA256</c>) request signing. Only the mandatory header set is signed -
/// <c>content-type</c>, <c>content-md5</c>, and every <c>x-oss-</c> header - which leaves AdditionalHeaders empty and
/// keeps a proxy-injected transport header from invalidating a signature. The payload is always declared
/// <c>UNSIGNED-PAYLOAD</c>: an upload is streamed, so its digest cannot be known before the body is sent.
/// </summary>
internal static class AliyunOssV4Signer
{
    public const string Algorithm = "OSS4-HMAC-SHA256";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string DateHeader = "x-oss-date";
    public const string ContentSha256Header = "x-oss-content-sha256";
    public const string SecurityTokenHeader = "x-oss-security-token";

    private const string Product = "oss";
    private const string RequestType = "aliyun_v4_request";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private static readonly System.Buffers.SearchValues<char> Unreserved = System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~");

    public static string Timestamp(DateTimeOffset instant) => instant.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static string Authorization(AliyunOssSigningRequest request, AliyunOssSigningIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);

        var timestamp = Timestamp(request.Timestamp);
        var scope = $"{timestamp[..8]}/{identity.Region}/{Product}/{RequestType}";
        var stringToSign = StringToSign(CanonicalRequest(request), timestamp, scope);
        var signature = Convert.ToHexStringLower(Hmac(SigningKey(identity, timestamp[..8]), stringToSign));

        return $"{Algorithm} Credential={identity.AccessKeyId}/{scope},Signature={signature}";
    }

    private static string CanonicalRequest(AliyunOssSigningRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(request.Method).Append('\n');
        builder.Append(Encode(request.ResourcePath, escapeSlash: false)).Append('\n');
        builder.Append(CanonicalQuery(request.Query)).Append('\n');

        foreach (var header in SignedHeaders(request.Headers)) builder.Append(header.Key).Append(':').Append(header.Value).Append('\n');

        return builder.Append('\n').Append(UnsignedPayload).ToString();
    }

    private static string StringToSign(string canonicalRequest, string timestamp, string scope) =>
        $"{Algorithm}\n{timestamp}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

    /// <summary>The mandatory signed set: content assertions plus every provider header, lower-cased and ordinal-sorted.</summary>
    private static SortedDictionary<string, string> SignedHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (name is "content-type" or "content-md5" || name.StartsWith("x-oss-", StringComparison.Ordinal)) signed[name] = header.Value.Trim();
        }

        return signed;
    }

    private static string CanonicalQuery(IReadOnlyDictionary<string, string> query) =>
        string.Join('&', query.Select(entry => (Key: Encode(entry.Key, escapeSlash: true), Value: Encode(entry.Value, escapeSlash: true)))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"));

    private static byte[] SigningKey(AliyunOssSigningIdentity identity, string date)
    {
        var key = Hmac(identity.SigningKeySeed, date);
        key = Hmac(key, identity.Region);
        key = Hmac(key, Product);
        return Hmac(key, RequestType);
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>RFC 3986 percent-encoding over UTF-8 bytes; OSS keeps the path separator literal in the canonical URI.</summary>
    public static string Encode(string value, bool escapeSlash)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var character = (char)b;
            if (Unreserved.Contains(character) || (character == '/' && !escapeSlash)) builder.Append(character);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

/// <summary>One request reduced to exactly the material OSS V4 canonicalizes. Header names are matched case-insensitively.</summary>
internal sealed record AliyunOssSigningRequest
{
    public required string Method { get; init; }

    /// <summary>The unescaped canonical resource, always <c>/{bucket}/{key}</c> even under virtual-hosted addressing.</summary>
    public required string ResourcePath { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Signing identity. <see cref="SigningKeySeed"/> is UTF-8 of <c>"aliyun_v4" + accessKeySecret</c>, held as bytes so the
/// driver can zero it on dispose and never has to keep the secret in a form a diagnostic could print.
/// </summary>
internal sealed record AliyunOssSigningIdentity
{
    public required string Region { get; init; }
    public required string AccessKeyId { get; init; }
    public required byte[] SigningKeySeed { get; init; }
    public string? SecurityToken { get; init; }

    public override string ToString() => $"AliyunOssSigningIdentity {{ Region = {Region}, AccessKeyId = {AccessKeyId}, SigningKeySeed = [REDACTED], SecurityToken = [REDACTED] }}";
}
