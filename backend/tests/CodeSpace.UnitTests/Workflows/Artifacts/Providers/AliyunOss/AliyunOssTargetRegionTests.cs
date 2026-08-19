using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Pins where the OSS4-HMAC-SHA256 signing region comes from. The region is load-bearing - it is hashed into the
/// signing key - but it is not a value the operator holds separately from the endpoint, so the target derives it and
/// keeps an explicit <c>region</c> only as an override. The refusal cases matter as much as the derivations: a wrong
/// region produces a signature OSS rejects, which surfaces as an authentication error rather than as the
/// configuration error it actually is, so anything not derivable must fail at parse time naming the field to supply.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssTargetRegionTests
{
    [Theory]
    [InlineData("oss-cn-hangzhou.aliyuncs.com", "cn-hangzhou")]
    [InlineData("oss-cn-hangzhou-internal.aliyuncs.com", "cn-hangzhou")]
    [InlineData("oss-ap-southeast-1.aliyuncs.com", "ap-southeast-1")]
    [InlineData("oss-ap-southeast-1-internal.aliyuncs.com", "ap-southeast-1")]
    [InlineData("oss-cn-shanghai-finance-1.aliyuncs.com", "cn-shanghai-finance-1")]
    // The schema admits the scheme-qualified spelling of the same host, so it must derive the same region.
    [InlineData("https://oss-cn-hangzhou.aliyuncs.com", "cn-hangzhou")]
    public void An_oss_service_endpoint_yields_the_region_it_names(string endpoint, string expectedRegion)
    {
        Parse(endpoint).Region.ShouldBe(expectedRegion);
    }

    [Theory]
    // The global and non-mainland transfer-acceleration hosts: the label after oss- is a service name, and signing
    // with it would produce a rejected signature instead of a visible configuration error.
    [InlineData("oss-accelerate.aliyuncs.com")]
    [InlineData("oss-accelerate-overseas.aliyuncs.com")]
    // A custom domain or CNAME carries no region at all, whether or not it looks like a service host.
    [InlineData("artifacts.example.com")]
    [InlineData("oss-cn-hangzhou.example.com")]
    // An extra label between the oss- prefix and the service domain is not a service endpoint whose region is known.
    [InlineData("codespace-artifacts.oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("oss-cn-hangzhou.oss.aliyuncs.com")]
    public void An_endpoint_that_names_no_region_is_refused_with_the_field_to_supply(string endpoint)
    {
        var error = Should.Throw<ArgumentException>(() => Parse(endpoint));

        error.Message.ShouldContain("'region'", Case.Sensitive, "the refusal has to name the field an operator must fill in, not merely report that signing is impossible");
        error.Message.ShouldContain("cn-hangzhou", Case.Sensitive, "an example region id tells the operator what shape of value to supply");
        error.Message.ShouldContain(endpoint, Case.Sensitive, "naming the host that could not be read is what stops the operator hunting through the wrong field");
        error.ParamName.ShouldBe("configuration");
    }

    [Fact]
    public void An_explicit_region_overrides_the_one_the_endpoint_names()
    {
        Parse("oss-cn-hangzhou.aliyuncs.com", region: "cn-shanghai").Region.ShouldBe("cn-shanghai", "an already-configured profile keeps signing with exactly the region it recorded");
        Parse("artifacts.example.com", region: "cn-hangzhou").Region.ShouldBe("cn-hangzhou", "the override is what makes a custom domain configurable at all");
    }

    /// <summary>
    /// The whole point of deriving is that the derived value IS the value: it has to hash into the same signing key.
    /// Comparing two authorization headers alone could pass on two equally wrong regions, so this also pins the region
    /// inside the credential scope and pins that a different region really does move the signature.
    /// </summary>
    [Fact]
    public void A_derived_region_signs_exactly_like_the_same_region_supplied_explicitly()
    {
        var derived = Identity(Parse("oss-cn-hangzhou.aliyuncs.com").Region);
        var explicitly = Identity(Parse("oss-cn-hangzhou.aliyuncs.com", region: "cn-hangzhou").Region);

        var authorization = AliyunOssV4Signer.Authorization(Request(), derived);

        authorization.ShouldBe(AliyunOssV4Signer.Authorization(Request(), explicitly));
        authorization.ShouldContain("/20260818/cn-hangzhou/oss/aliyun_v4_request", Case.Sensitive, "the derived region has to reach the V4 credential scope, not just match another derived value");
        authorization.ShouldNotBe(AliyunOssV4Signer.Authorization(Request(), Identity("cn-shanghai")), "the signing key is region-scoped, so an equality assertion over it is only meaningful if a different region moves it");
    }

    private static AliyunOssTarget Parse(string endpoint, string? region = null)
    {
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["endpoint"] = endpoint, ["bucket"] = "codespace-artifacts" };
        if (region != null) configuration["region"] = region;

        return AliyunOssTarget.Parse(JsonSerializer.SerializeToElement(configuration));
    }

    private static AliyunOssSigningIdentity Identity(string region) => new()
    {
        Region = region,
        AccessKeyId = "LTAI5tFakeAccessKeyId",
        SigningKeySeed = Encoding.UTF8.GetBytes("aliyun_v4wJalrXUtnFEMIK7MDENGbPxRfiCYFAKESECRET")
    };

    private static AliyunOssSigningRequest Request() => new()
    {
        Method = "PUT",
        ResourcePath = "/codespace-artifacts/codespace/object",
        Query = new Dictionary<string, string>(StringComparer.Ordinal),
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-oss-content-sha256"] = "UNSIGNED-PAYLOAD", ["x-oss-date"] = "20260818T123456Z" },
        Timestamp = new DateTimeOffset(2026, 8, 18, 12, 34, 56, TimeSpan.Zero)
    };
}
