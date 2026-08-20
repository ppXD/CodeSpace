using System.Reflection;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Pins the opt-in real-bucket lane's two promises that CAN be checked without a bucket: it SKIPS cleanly with the
/// variables unset, and it cannot publish the secret. Runs in the normal unit suite, unlike the lane it guards - a
/// skip path nobody exercises is a skip path nobody notices breaking.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssRealBucketLaneTests
{
    private const string Secret = "wJalrXUtnFEMI-not-a-real-secret";
    private const string KeyId = "LTAI-not-a-real-key-id";

    /// <summary>
    /// An operator pastes these once. A rename does not fail - it silently disables the check and leaves eight green
    /// no-ops that read like a pass - so both the literals and the ORDER the skip line names them in are pinned here,
    /// and a fifth variable cannot arrive without landing in this list.
    /// </summary>
    [Fact]
    public void Every_variable_the_lane_reads_is_pinned_to_its_literal()
    {
        AliyunOssRealBucket.EnvVars.ShouldBe(new[]
        {
            "CODESPACE_OSS_BUCKET_NAME",
            "CODESPACE_OSS_ENDPOINT",
            "CODESPACE_OSS_ACCESS_KEY_ID",
            "CODESPACE_OSS_ACCESS_KEY_SECRET"
        });

        AliyunOssRealBucket.KeyPrefixRoot.ShouldBe("codespace-conformance/", "an operator hunting an abandoned run's leftovers greps for this prefix");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_variable_that_is_absent_or_blank_leaves_the_lane_unconfigured(string? raw)
    {
        string? Read(string name) => name == AliyunOssRealBucket.AccessKeySecretEnvVar ? raw : "supplied";

        AliyunOssRealBucket.TryRead(Read).ShouldBeNull();
        AliyunOssRealBucket.Unset(Read).ShouldBe(new[] { AliyunOssRealBucket.AccessKeySecretEnvVar });
    }

    [Fact]
    public void The_skip_report_names_every_unset_variable_and_denies_being_a_pass()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oss-real-bucket-skip-{Guid.NewGuid():N}.md");

        AliyunOssRealBucket.ReportSkipped(AliyunOssRealBucket.Unset(_ => null), path);

        var line = File.ReadAllText(path);
        File.Delete(path);
        line.ShouldContain("NOT VERIFIED");
        line.ShouldContain("skip is NOT a pass");
        foreach (var name in AliyunOssRealBucket.EnvVars) line.ShouldContain(name);
    }

    /// <summary>
    /// The skip contract end to end: with nothing configured, every case the lane INHERITS completes as a green no-op.
    /// It invokes the inherited cases directly rather than trusting that each one carries the guard - remove any single
    /// <c>StoreIsReachable</c> guard from the shared kit and this reds, which is how that guard was verified.
    /// </summary>
    [Fact]
    public async Task Without_credentials_every_inherited_conformance_case_is_a_green_no_op()
    {
        var lane = new AliyunOssRealBucketConformanceTests(_ => null);
        var cases = typeof(ArtifactStorageDriverConformanceTests).GetMethods().Where(method => method.GetCustomAttribute<FactAttribute>() != null).ToList();

        cases.ShouldNotBeEmpty();
        foreach (var one in cases) await (Task)one.Invoke(lane, null)!;

        await lane.DisposeAsync();
    }

    /// <summary>
    /// The fake-backed lanes must never be skippable. Only the real-service lane may declare its store unreachable, so
    /// a green-skip can never spread from it to the suites that are supposed to run everywhere.
    /// </summary>
    [Fact]
    public void Only_the_real_bucket_lane_declares_its_store_unreachable()
    {
        var suites = typeof(ArtifactStorageDriverConformanceTests).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && typeof(ArtifactStorageDriverConformanceTests).IsAssignableFrom(type))
            .ToList();

        var overriding = suites.Where(type => Reachability(type).DeclaringType != typeof(ArtifactStorageDriverConformanceTests)).ToList();

        suites.Count.ShouldBeGreaterThan(1, "the kit is inherited by the fake-backed lanes too; a suite list of one means this guard is watching nothing");
        overriding.ShouldBe(new[] { typeof(AliyunOssRealBucketConformanceTests) });
    }

    /// <summary>
    /// Two ways the secret could escape by accident: a record's generated <c>ToString</c> printing every property, and
    /// the credential leaking into the profile CONFIGURATION - the non-secret half, which is the half that gets
    /// persisted and surfaced.
    /// </summary>
    [Fact]
    public void Neither_the_settings_value_nor_the_activation_profile_can_echo_the_credential()
    {
        var settings = AliyunOssRealBucket.TryRead(Distinct)!;

        var profile = AliyunOssRealBucket.Profile(settings, "codespace-conformance/pinned/");

        settings.ToString().ShouldNotContain(Secret);
        settings.ToString().ShouldNotContain(KeyId);
        profile.Configuration.ToString().ShouldNotContain(Secret);
        profile.Configuration.ToString().ShouldNotContain(KeyId, Case.Sensitive, "the profile's configuration is the half that gets persisted and surfaced; only the credential handle may carry AccessKey material");
    }

    /// <summary>
    /// The absent-destination seam defaults to null, which makes its case a green no-op, so it would go dead in total
    /// silence if the overrides were ever dropped. Both are pinned: the fake-backed OSS lane, which proves the driver
    /// re-asks, and the real-bucket lane, which is the only thing that can prove a real OSS HEAD carries no body to
    /// re-ask about. The local RWX driver is deliberately absent from this list - it creates its own root, so it has no
    /// absent destination to answer for.
    /// </summary>
    [Fact]
    public void Both_lanes_that_can_reach_an_absent_destination_supply_one()
    {
        var suites = typeof(ArtifactStorageDriverConformanceTests).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && typeof(ArtifactStorageDriverConformanceTests).IsAssignableFrom(type))
            .ToList();

        var supplying = suites.Where(type => AbsentDestination(type).DeclaringType != typeof(ArtifactStorageDriverConformanceTests)).ToList();

        supplying.ShouldBe(new[] { typeof(AliyunOssArtifactStorageDriverContractTests), typeof(AliyunOssRealBucketConformanceTests) }, ignoreOrder: true);
    }

    /// <summary>A distinct value per variable, so an assertion that one did not leak cannot pass on another's value.</summary>
    private static string Distinct(string name) => name switch
    {
        AliyunOssRealBucket.BucketNameEnvVar => "pinned-bucket",
        AliyunOssRealBucket.EndpointEnvVar => "oss-cn-hangzhou.aliyuncs.com",
        AliyunOssRealBucket.AccessKeyIdEnvVar => KeyId,
        _ => Secret
    };

    private static MethodInfo Reachability(Type suite) =>
        suite.GetProperty("StoreIsReachable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetGetMethod(nonPublic: true)!;

    private static MethodInfo AbsentDestination(Type suite) =>
        suite.GetMethod("CreateDriverOverAbsentDestinationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
}
