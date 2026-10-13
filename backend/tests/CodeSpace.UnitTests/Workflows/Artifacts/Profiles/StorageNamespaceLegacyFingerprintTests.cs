using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Profiles;

/// <summary>
/// The fingerprint a revision STORES, held to the value the previous build stored for the same configuration.
///
/// <para>The previous build's retention reaper decides whether one object key is one object across two teams by
/// comparing stored fingerprints, and a rolling deploy or a rollback runs that reaper over revisions this build
/// admitted. So for one release this build stores exactly what that build stored — the hash of the configuration as
/// written — or a second team's placement under the same typed root reads to that reaper as another namespace, and it
/// deletes the first team's live bytes.</para>
///
/// <para>The expected values were produced by the previous build itself (583efb0df: its own admission arithmetic,
/// <c>GetNamespaceConfiguration</c> then <c>NamespaceFingerprint</c>, loaded from its compiled assembly and run on
/// these configurations), never by this build's code, so a change to canonicalization, to the hash input or to which
/// value admission stores fails here.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageNamespaceLegacyFingerprintTests
{
    private static readonly IStorageProviderModule[] Modules = [new LocalRwxStorageProviderModule(), new LocalLegacyStorageProviderModule(), new AliyunOssStorageProviderModule()];

    public static TheoryData<string, string, string> PreviousBuildFingerprints => new()
    {
        { "local-rwx/v1", """{"rootPath":"/srv/shared-bucket"}""", "sha256:093f1366bc44ad1ab2d3f00d3e3ec39cbb739d823c3938a37ab735250ec4a9e4" },
        { "local-rwx/v1", """{"rootPath":"/srv/shared-bucket/"}""", "sha256:a2d502242c785d4dfe940fde7d7f526933cbf0f7267a802e379fdb72847585a3" },
        { "local-rwx/v1", """{"rootPath":"/srv/shared-bucket/."}""", "sha256:c96998794d1302bd05a8938754c029d563895f1c8cd373610ced5e48808ed979" },
        { "local-rwx/v1", """{"rootPath":"/srv//shared-bucket"}""", "sha256:225b05ee64045303133a910b9db6661f9aa7ef8ac64f287e3018c107da504fe0" },
        { "local-rwx/v1", """{"rootPath":"/var/lib/codespace/artifacts"}""", "sha256:cb775836c24c2af3263152dd023982abffc39fc989ef5b68f0ad556ef2c31b6c" },
        { "local-rwx/v1", """{"rootPath":"/tmp/codespace-store"}""", "sha256:7039c5008170b4b1dc438ae9097259d0b5aa82f38774f8a2bb30db32bcc9fe6d" },
        { "local-rwx/v1", """{"rootPath":"/srv/Shared-Bucket"}""", "sha256:d76573877d14a4005892967e72e3c26839a7d4210a277bdb6830ab0d57c6dce0" },
        { "local-rwx/v1", """{"rootPath":"/srv/café"}""", "sha256:23bcf70b1b16176638d65aa5d5f1d0250b3ea712fed74741ee3a1efef35eb4d7" },
        { "local-rwx/v1", """{ "rootPath" : "/srv/shared-bucket/" }""", "sha256:a2d502242c785d4dfe940fde7d7f526933cbf0f7267a802e379fdb72847585a3" },
        { "local-legacy/v1", """{"rootPath":"/var/lib/codespace/artifacts"}""", "sha256:248d13d1e0dd50d09c3f4482550e28dab37aba148c43cdefa437e8b7c3288e25" },
        { "local-legacy/v1", """{"rootPath":"/var/lib/codespace/artifacts/"}""", "sha256:fc5f46c23028c33a9716394dd8f82f15150e86d469a909c7cedd8b221564e19d" },
        { "aliyun-oss/v1", """{"bucket":"acme-artifacts","endpoint":"oss-cn-hangzhou.aliyuncs.com"}""", "sha256:c4f957bc326598c5be60c0cb477d88fc8206a44a711d75b836514daa55233c56" },
        { "aliyun-oss/v1", """{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"acme-artifacts"}""", "sha256:c4f957bc326598c5be60c0cb477d88fc8206a44a711d75b836514daa55233c56" },
        { "aliyun-oss/v1", """{"bucket":"acme-artifacts","endpoint":"oss-cn-hangzhou-internal.aliyuncs.com"}""", "sha256:c53891fdffda0aaab403e48377feb988edd9f7e3cfbbd8992f040f6d64c113fa" },
        { "aliyun-oss/v1", """{"bucket":"acme-artifacts","endpoint":"https://oss-cn-hangzhou.aliyuncs.com","keyPrefix":"codespace/"}""", "sha256:cfceea5c7919d60c0b3de79fac8f1146137cf9be4f5813d5b20c267c12956dcd" },
        { "aliyun-oss/v1", """{"bucket":"acme-artifacts","endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou"}""", "sha256:8948c27532243bb3b55f8193b5fbf1f47a324dc7427b006b9d2b1a7427801af9" },
        { "aliyun-oss/v1", """{"bucket":"acme-artifacts","endpoint":"oss-cn-hangzhou.aliyuncs.com","keyPrefix":"team-7/"}""", "sha256:5fb52e872c5e3d1e286775a1095767457b8636131268f6aadd2ff50210606015" },
    };

    [Theory]
    [MemberData(nameof(PreviousBuildFingerprints))]
    public void The_legacy_fingerprint_is_the_one_the_previous_build_stored(string providerTypeKey, string config, string previousBuild)
    {
        using var document = JsonDocument.Parse(config);

        StorageProfileRules.LegacyNamespaceFingerprint(providerTypeKey, document.RootElement).ShouldBe(previousBuild);
    }

    [Theory]
    [MemberData(nameof(PreviousBuildFingerprints))]
    public void Admission_stores_what_the_previous_build_stored_for_every_shipped_module(string providerTypeKey, string config, string previousBuild)
    {
        // Admission hands the provider the CANONICAL configuration, so this does too.
        using var typed = JsonDocument.Parse(config);
        using var canonical = JsonDocument.Parse(StorageProfileRules.CanonicalJson(typed.RootElement));

        StorageProfileRules.StoredNamespaceFingerprint(Module(providerTypeKey), canonical.RootElement).ShouldBe(previousBuild,
            "a revision admitted now must compare equal, in the previous build's reaper, to one that build admitted from the same text");
    }

    [Fact]
    public void The_stored_value_is_not_the_physical_identity_for_a_configuration_written_another_way()
    {
        // The precondition that makes the golden theory above a regression test: for a trailing separator the physical
        // identity and the stored one differ, so storing the physical one would fail it.
        using var config = JsonDocument.Parse("""{"rootPath":"/srv/shared-bucket/"}""");
        var module = Module("local-rwx/v1");

        StorageProfileRules.StoredNamespaceFingerprint(module, config.RootElement).ShouldNotBe(StorageProfileRules.NamespaceFingerprint(module, config.RootElement));
    }

    [Fact]
    public void A_configuration_that_reaches_no_namespace_is_still_refused_rather_than_given_a_legacy_identity()
    {
        // Admission has refused a root that names no directory since namespaces were derived physically. Storing the
        // legacy value must not quietly re-admit it.
        using var blank = JsonDocument.Parse("""{"rootPath":"   "}""");

        Should.Throw<ArgumentException>(() => StorageProfileRules.StoredNamespaceFingerprint(Module("local-rwx/v1"), blank.RootElement));
    }

    private static IStorageProviderModule Module(string providerTypeKey) => Modules.Single(module => module.TypeKey == providerTypeKey);
}
