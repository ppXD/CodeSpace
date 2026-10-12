using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The namespace identity of an OSS destination is the bucket and key prefix it reaches, not the route it reaches them
/// by. The public host, the VPC host and the accelerate host of one region all address the same bucket, so two profiles
/// that spell the route differently still share every object key — and object keys carry no team segment.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssStorageNamespaceTests
{
    private const string Canonical = """{"bucket":"codespace-artifacts","endpoint":"oss-cn-hangzhou.aliyuncs.com","keyPrefix":"team-7/"}""";

    [Theory]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"https://oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou-internal.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"https://oss-cn-hangzhou-internal.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"oss-accelerate.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"oss-accelerate-overseas.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    public void Every_route_to_one_bucket_and_prefix_projects_to_one_namespace(string spelling) =>
        Projection(spelling).ShouldBe(Canonical);

    [Fact]
    public void An_empty_key_prefix_is_the_bucket_root_and_so_is_no_key_prefix_at_all() =>
        Projection("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":""}""")
            .ShouldBe(Projection("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts"}"""));

    [Theory]
    [InlineData("""{"endpoint":"https://files.example.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"oss.example.internal","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    public void A_host_outside_the_service_domain_is_one_namespace_with_the_bucket_of_the_region_it_signs_for(string spelling) =>
        // A custom domain or proxy cannot say which bucket it fronts, and where the identity cannot tell two routes
        // apart it must merge them: OSS bucket names are unique service-wide and every request is signed for the
        // bucket's own region, so the only OSS bucket this route can reach is that region's bucket of the same name.
        Projection(spelling).ShouldBe(Canonical);

    [Theory]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"other-bucket","keyPrefix":"team-7/"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-9/"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-7/nested/"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts"}""")]
    [InlineData("""{"endpoint":"oss-cn-shanghai.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""")]
    public void A_different_bucket_prefix_or_region_stays_a_different_namespace(string other) =>
        // The guard on the guard: an identity that merged these would keep every deduplicated byte forever.
        Projection(other).ShouldNotBe(Canonical);

    [Theory]
    [InlineData("a//b/")]
    [InlineData("./a/")]
    [InlineData("a/./b/")]
    [InlineData("a/../b/")]
    [InlineData("/a/")]
    [InlineData("a")]
    public void A_key_prefix_the_driver_would_refuse_has_no_identity_rather_than_an_invented_one(string keyPrefix)
    {
        // OSS keys are literal, so 'a//b/' and 'a/b/' are different objects; the activation parser refuses both kinds of
        // ambiguity outright rather than normalising them. The projection reuses that parser, so a revision carrying
        // one has no identity at all — and a caller comparing identities must read that as "possibly the same".
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(new { endpoint = "oss-cn-hangzhou.aliyuncs.com", bucket = "codespace-artifacts", keyPrefix }));

        Should.Throw<ArgumentException>(() => Module.GetNamespaceConfiguration(config.RootElement));
    }

    [Fact]
    public void A_configuration_already_written_canonically_keeps_the_fingerprint_its_revision_stored()
    {
        // Hard-pinned: the value an aliyun-oss revision written this way has carried since the fingerprint was
        // introduced. Revisions are immutable, so a change here silently re-means every one of them.
        using var config = JsonDocument.Parse("""{"keyPrefix":"team-7/","bucket":"codespace-artifacts","endpoint":"oss-cn-hangzhou.aliyuncs.com"}""");

        StorageProfileRules.NamespaceFingerprint(Module, config.RootElement).ShouldBe(StorageProfileRules.NamespaceFingerprint(Module.TypeKey, config.RootElement));
        StorageProfileRules.NamespaceFingerprint(Module, config.RootElement).ShouldBe("sha256:a1c39ca50714a2497bf4b94f95aaae1fa2eace6c1674c11163def0f8c8b03261");
    }

    private static IStorageProviderModule Module { get; } = new AliyunOssStorageProviderModule();

    private static string Projection(string config)
    {
        using var document = JsonDocument.Parse(config);

        return StorageProfileRules.CanonicalJson(Module.GetNamespaceConfiguration(document.RootElement));
    }
}
