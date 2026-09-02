using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Deterministic in-memory stand-in for the Aliyun OSS object API, so the shared driver conformance kit runs in CI
/// with no bucket, no network, and no credentials. It models only what the driver actually speaks: virtual-hosted
/// addressing, streaming PUT, server-side copy with <c>x-oss-forbid-overwrite</c>, HEAD, ranged/conditional GET,
/// DELETE, and the ListObjectsV2 probe - plus the OSS XML error envelope. It deliberately does NOT recompute the
/// request signature (that would only prove the SDK agrees with itself); it asserts the wire-visible shape of the
/// official SDK's V4 authorization material instead.
///
/// Every write and every read carries <c>x-oss-version-id</c>, so the driver is exercised against a response shape
/// that reports versions: a version the driver reported but would not accept back as an <c>ExpectedVersion</c> is a
/// permanently broken profile, and a fixture that never emits the header cannot see it.
///
/// It models that RESPONSE SHAPE only, NOT a versioning-enabled bucket's semantics. DELETE here purges the key
/// outright; a real versioned bucket would insert a delete marker and keep the prior version's bytes. So the
/// staging-leaves-nothing-behind assertions hold on a versioning-DISABLED bucket, which is the shape this driver is
/// written for (see the driver's own class doc). Modelling delete markers faithfully would turn those assertions red,
/// which is the correct next step whenever the driver learns to discard by version id.
/// The STS token is optional here exactly as it is in the secret schema, so both credential shapes reach the wire.
/// </summary>
public sealed class FakeAliyunOssHandler : HttpMessageHandler
{
    public const string Bucket = "codespace-artifacts";
    public const string Region = "cn-hangzhou";
    public const string Host = "oss-cn-hangzhou.aliyuncs.com";
    public const string AccessKeyId = "LTAI5tFakeAccessKeyId";
    public const string AccessKeySecret = "wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET";
    public const string SecurityToken = "CAISzgJ1q6Ft5B2yfSjIr5fFFAKESTSTOKEN";

    private static readonly Regex AuthorizationPattern = new(@"^Credential=(?<ak>[^/,]+)/(?<date>\d{8})/(?<region>[^/,]+)/oss/aliyun_v4_request,Signature=(?<signature>[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex TimestampPattern = new(@"^\d{8}T\d{6}Z$", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public List<string> Authorizations { get; } = [];
    public List<string> Calls { get; } = [];
    public List<string?> Rfc822Dates { get; } = [];
    public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>One entry per authorized request: the <c>x-oss-security-token</c> it carried, or null when it carried none.</summary>
    public List<string?> SecurityTokens { get; } = [];

    /// <summary>Forces every request to fail the way OSS fails a bad signature, body echo included.</summary>
    public bool RejectEverySignature { get; set; }
    public bool BlockEveryRequest { get; set; }

    /// <summary>Empties the bucket without touching the recorded calls — the shape of an object deleted outside CodeSpace.</summary>
    public void EmptyBucket() => _objects.Clear();

    public IReadOnlyCollection<string> Keys { get { lock (_gate) return _objects.Keys.ToList(); } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BlockEveryRequest)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        var uri = request.RequestUri!;
        var key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        lock (_gate) Calls.Add($"{request.Method.Method} /{key}{uri.Query}");

        var response = await RouteAsync(request, uri, key, cancellationToken).ConfigureAwait(false);

        return request.Method == HttpMethod.Head ? Bodiless(response) : response;
    }

    private async Task<HttpResponseMessage> RouteAsync(HttpRequestMessage request, Uri uri, string key, CancellationToken cancellationToken)
    {
        var rejection = Authorize(request);
        if (rejection != null) return rejection;
        if (!string.Equals(uri.Host, $"{Bucket}.{Host}", StringComparison.Ordinal)) return Error(HttpStatusCode.NotFound, "NoSuchBucket");
        if (key.Length == 0) return List();

        if (request.Method == HttpMethod.Put && request.Headers.TryGetValues("x-oss-copy-source", out var source)) return Copy(source.Single(), key, request);
        if (request.Method == HttpMethod.Put) return await PutAsync(key, request, cancellationToken).ConfigureAwait(false);
        if (request.Method == HttpMethod.Head) return Head(key);
        if (request.Method == HttpMethod.Get) return Get(key, request);
        if (request.Method == HttpMethod.Delete) return Delete(key);
        return Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    /// <summary>
    /// Strips the body from a HEAD response while keeping every header a GET would carry, Content-Length included -
    /// which is what HTTP requires and therefore what the real service does, on the success path AND on every error
    /// path. It matters because OSS puts its <c>&lt;Code&gt;</c> token in the body ONLY: a fixture that answers a HEAD
    /// with an XML error envelope hands the driver a discriminator no real HEAD can ever supply, and every
    /// classification branch that reads one then passes here and is unreachable in production.
    /// </summary>
    private static HttpResponseMessage Bodiless(HttpResponseMessage response)
    {
        var length = response.Content.Headers.ContentLength;
        var bodiless = new ByteArrayContent([]);
        foreach (var header in response.Content.Headers) bodiless.Headers.TryAddWithoutValidation(header.Key, header.Value);

        bodiless.Headers.ContentLength = length;
        response.Content = bodiless;
        return response;
    }

    private HttpResponseMessage? Authorize(HttpRequestMessage request)
    {
        var authorization = request.Headers.Authorization;
        if (authorization == null || !string.Equals(authorization.Scheme, "OSS4-HMAC-SHA256", StringComparison.Ordinal)) return SignatureRejection();

        lock (_gate) Authorizations.Add($"{authorization.Scheme} {authorization.Parameter}");
        var parsed = AuthorizationPattern.Match(authorization.Parameter ?? string.Empty);
        if (!parsed.Success || parsed.Groups["ak"].Value != AccessKeyId || parsed.Groups["region"].Value != Region) return SignatureRejection();

        var timestamp = Single(request, "x-oss-date");
        if (timestamp == null || !TimestampPattern.IsMatch(timestamp) || timestamp[..8] != parsed.Groups["date"].Value) return SignatureRejection();
        if (Single(request, "x-oss-content-sha256") != "UNSIGNED-PAYLOAD") return SignatureRejection();
        lock (_gate) Rfc822Dates.Add(Single(request, "Date"));

        // A long-lived AccessKey sends no STS token at all, so only a token that is present and wrong is a rejection.
        var securityToken = Single(request, "x-oss-security-token");
        if (securityToken != null && securityToken != SecurityToken) return SignatureRejection();

        lock (_gate) SecurityTokens.Add(securityToken);

        return RejectEverySignature ? SignatureRejection() : null;
    }

    /// <summary>Mirrors the real OSS 403 body, which echoes the access key id and the server's own StringToSign.</summary>
    private static HttpResponseMessage SignatureRejection() => Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch",
        $"<AccessKeyId>{AccessKeyId}</AccessKeyId><StringToSign>OSS4-HMAC-SHA256&#10;{AccessKeySecret}</StringToSign><SecurityToken>{SecurityToken}</SecurityToken>");

    private async Task<HttpResponseMessage> PutAsync(string key, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var stored = new StoredObject(bytes, ETag(bytes), VersionId(), request.Content?.Headers.ContentType?.ToString(), DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000), UserMetadata(request));

        lock (_gate)
        {
            if (Forbids(request) && _objects.ContainsKey(key)) return Error(HttpStatusCode.Conflict, "FileAlreadyExists");
            _objects[key] = stored;
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("ETag", stored.ETag);
        response.Headers.TryAddWithoutValidation("x-oss-version-id", stored.VersionId);
        return response;
    }

    private HttpResponseMessage Copy(string source, string destinationKey, HttpRequestMessage request)
    {
        var sourceKey = Uri.UnescapeDataString(source[$"/{Bucket}/".Length..]);

        lock (_gate)
        {
            if (!_objects.TryGetValue(sourceKey, out var stored)) return Error(HttpStatusCode.NotFound, "NoSuchKey");
            if (Forbids(request) && _objects.ContainsKey(destinationKey)) return Error(HttpStatusCode.Conflict, "FileAlreadyExists");

            // A copy writes a new object version, so the destination never inherits the staging upload's version id.
            var published = stored with { VersionId = VersionId() };
            _objects[destinationKey] = published;

            var body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><CopyObjectResult><ETag>{published.ETag}</ETag><LastModified>{published.LastModified.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}</LastModified></CopyObjectResult>";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
            response.Headers.TryAddWithoutValidation("x-oss-version-id", published.VersionId);
            return response;
        }
    }

    private HttpResponseMessage Head(string key)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(key, out var stored)) return Error(HttpStatusCode.NotFound, "NoSuchKey");
            return Describe(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }, stored, stored.Bytes.LongLength);
        }
    }

    private HttpResponseMessage Get(string key, HttpRequestMessage request)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(key, out var stored)) return Error(HttpStatusCode.NotFound, "NoSuchKey");
            if (request.Headers.IfMatch.Count != 0 && !request.Headers.IfMatch.Any(tag => tag.Tag == stored.ETag)) return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            if (request.Headers.Range == null) return Describe(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stored.Bytes) }, stored, stored.Bytes.LongLength);

            var range = request.Headers.Range.Ranges.Single();
            var offset = range.From!.Value;
            if (offset >= stored.Bytes.LongLength) return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange");

            var last = Math.Min(range.To ?? stored.Bytes.LongLength - 1, stored.Bytes.LongLength - 1);
            var slice = stored.Bytes.AsSpan((int)offset, (int)(last - offset + 1)).ToArray();
            var partial = Describe(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) }, stored, slice.LongLength);
            partial.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {offset}-{last}/{stored.Bytes.LongLength}");
            return partial;
        }
    }

    private HttpResponseMessage Delete(string key)
    {
        lock (_gate) _objects.Remove(key);
        return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) };
    }

    private HttpResponseMessage List()
    {
        lock (_gate)
        {
            var body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><Name>{Bucket}</Name><KeyCount>{_objects.Count}</KeyCount><MaxKeys>1</MaxKeys><IsTruncated>{(_objects.Count > 1).ToString().ToLowerInvariant()}</IsTruncated></ListBucketResult>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
        }
    }

    private static HttpResponseMessage Describe(HttpResponseMessage response, StoredObject stored, long contentLength)
    {
        response.Headers.TryAddWithoutValidation("ETag", stored.ETag);
        response.Headers.TryAddWithoutValidation("x-oss-version-id", stored.VersionId);
        response.Headers.TryAddWithoutValidation("Last-Modified", stored.LastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        foreach (var entry in stored.Metadata) response.Headers.TryAddWithoutValidation($"x-oss-meta-{entry.Key}", entry.Value);
        if (stored.ContentType != null) response.Content.Headers.TryAddWithoutValidation("Content-Type", stored.ContentType);
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string extra = "")
    {
        var body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code} was returned by the fake OSS endpoint.</Message><RequestId>fake-request-id</RequestId>{extra}</Error>";
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
    }

    private static bool Forbids(HttpRequestMessage request) => Single(request, "x-oss-forbid-overwrite") == "true";

    private static string? Single(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static Dictionary<string, string> UserMetadata(HttpRequestMessage request) => request.Headers
        .Where(header => header.Key.StartsWith("x-oss-meta-", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(header => header.Key["x-oss-meta-".Length..], header => string.Join(",", header.Value), StringComparer.Ordinal);

    private static string ETag(byte[] bytes) => $"\"{Convert.ToHexString(MD5.HashData(bytes))}\"";

    /// <summary>Shaped like a real OSS version id: opaque, unique per write, and never derived from the content.</summary>
    private static string VersionId() => $"CAEQ{Guid.NewGuid():N}";

    private sealed record StoredObject(byte[] Bytes, string ETag, string VersionId, string? ContentType, DateTimeOffset LastModified, Dictionary<string, string> Metadata);
}
