using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Pins the OSS4-HMAC-SHA256 canonical request and key derivation by recomputing them independently here. The fake
/// endpoint deliberately does not verify signatures, so this is the only place that would catch a canonicalization
/// drift (path escaping, which headers are signed, ordering, the empty AdditionalHeaders line).
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssV4SignerTests
{
    private const string AccessKeyId = "LTAI5tFakeAccessKeyId";
    private const string AccessKeySecret = "wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET";
    private const string Region = "cn-hangzhou";
    private const string Timestamp = "20260818T123456Z";
    private const string Scope = "20260818/cn-hangzhou/oss/aliyun_v4_request";

    [Fact]
    public void Authorization_signs_the_canonical_request_the_oss_v4_contract_defines()
    {
        var authorization = AliyunOssV4Signer.Authorization(Request(), Identity());

        var canonicalRequest = string.Join("\n",
            "PUT",
            "/examplebucket/example%20object/%E6%BC%A2",
            "list-type=2&max-keys=0",
            "content-md5:eB5eJF1ptWaXm4bijSPyxw==",
            "content-type:text/html",
            "x-oss-content-sha256:UNSIGNED-PAYLOAD",
            "x-oss-date:" + Timestamp,
            "x-oss-meta-author:alice",
            string.Empty,
            "UNSIGNED-PAYLOAD");
        authorization.ShouldBe(Expected(canonicalRequest));
    }

    [Fact]
    public void Only_content_and_x_oss_headers_are_signed_so_host_and_transport_headers_cannot_break_a_signature()
    {
        var withoutTransportHeaders = Request(headers => { headers.Remove("Host"); headers.Remove("User-Agent"); });

        AliyunOssV4Signer.Authorization(withoutTransportHeaders, Identity()).ShouldBe(AliyunOssV4Signer.Authorization(Request(), Identity()));
    }

    [Fact]
    public void A_changed_x_oss_header_changes_the_signature()
    {
        var forbidOverwrite = Request(headers => headers["x-oss-forbid-overwrite"] = "true");

        AliyunOssV4Signer.Authorization(forbidOverwrite, Identity()).ShouldNotBe(AliyunOssV4Signer.Authorization(Request(), Identity()));
    }

    [Fact]
    public void The_signing_key_is_scoped_to_date_region_and_product_so_a_stale_key_cannot_be_reused()
    {
        var tomorrow = Request(timestamp: new DateTimeOffset(2026, 8, 19, 12, 34, 56, TimeSpan.Zero));
        var otherRegion = Identity() with { Region = "cn-shanghai" };

        AliyunOssV4Signer.Authorization(tomorrow, Identity()).ShouldNotBe(AliyunOssV4Signer.Authorization(Request(), Identity()));
        AliyunOssV4Signer.Authorization(Request(), otherRegion).ShouldNotBe(AliyunOssV4Signer.Authorization(Request(), Identity()));
    }

    [Fact]
    public void The_signing_material_never_appears_in_the_authorization_header()
    {
        var authorization = AliyunOssV4Signer.Authorization(Request(), Identity());

        authorization.ShouldNotContain(AccessKeySecret);
        authorization.ShouldContain(AccessKeyId, Case.Sensitive, "the key ID is public signing material; only the secret must stay off the wire");
    }

    private static string Expected(string canonicalRequest)
    {
        var stringToSign = string.Join("\n", "OSS4-HMAC-SHA256", Timestamp, Scope, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("aliyun_v4" + AccessKeySecret), Encoding.UTF8.GetBytes("20260818"));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Region));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("oss"));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("aliyun_v4_request"));
        return $"OSS4-HMAC-SHA256 Credential={AccessKeyId}/{Scope},Signature={Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)))}";
    }

    private static AliyunOssSigningIdentity Identity() => new()
    {
        Region = Region,
        AccessKeyId = AccessKeyId,
        SigningKeySeed = Encoding.UTF8.GetBytes("aliyun_v4" + AccessKeySecret)
    };

    private static AliyunOssSigningRequest Request(Action<Dictionary<string, string>>? mutate = null, DateTimeOffset? timestamp = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = "examplebucket.oss-cn-hangzhou.aliyuncs.com",
            ["User-Agent"] = "codespace",
            ["Content-Type"] = "text/html",
            ["Content-MD5"] = "eB5eJF1ptWaXm4bijSPyxw==",
            ["x-oss-content-sha256"] = "UNSIGNED-PAYLOAD",
            ["x-oss-date"] = timestamp == null ? Timestamp : AliyunOssV4Signer.Timestamp(timestamp.Value),
            ["x-oss-meta-author"] = "alice"
        };
        mutate?.Invoke(headers);

        return new AliyunOssSigningRequest
        {
            Method = "PUT",
            ResourcePath = "/examplebucket/example object/漢",
            Query = new Dictionary<string, string>(StringComparer.Ordinal) { ["max-keys"] = "0", ["list-type"] = "2" },
            Headers = headers,
            Timestamp = timestamp ?? new DateTimeOffset(2026, 8, 18, 12, 34, 56, TimeSpan.Zero)
        };
    }
}
