using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers;

/// <summary>
/// The join each provider performs, and the property that makes the result mean something: two teams must never
/// compose the same namespace out of one root. Object keys carry no team segment, so a namespace two teams share
/// makes their identical content one physical object, and a per-team purge deletes by an ETag identical bytes share.
/// </summary>
public sealed class StorageProviderTeamNamespaceTests
{
    public static TheoryData<IStorageProviderTeamNamespace, string> Providers => new()
    {
        { new AliyunOssStorageProviderModule(), "keyPrefix" },
        { new LocalRwxStorageProviderModule(), "rootPath" },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void Every_provider_names_the_config_property_that_carries_its_namespace(IStorageProviderTeamNamespace provider, string expected) =>
        provider.TeamNamespaceProperty.ShouldBe(expected);

    [Theory]
    [MemberData(nameof(Providers))]
    public void Two_teams_never_compose_the_same_namespace_from_one_root(IStorageProviderTeamNamespace provider, string _)
    {
        var first = provider.ComposeTeamNamespace("codespace", "team-a");
        var second = provider.ComposeTeamNamespace("codespace", "team-b");

        first.ShouldNotBe(second, "two teams sharing a namespace share one physical object per identical payload");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void One_team_composes_the_same_namespace_every_time(IStorageProviderTeamNamespace provider, string _) =>
        // A namespace that drifted between two materializations of one team would strand every byte written under the
        // earlier one: the profile revision that recorded it is immutable, and reads resolve through it.
        provider.ComposeTeamNamespace("codespace", "team-a").ShouldBe(provider.ComposeTeamNamespace("codespace", "team-a"));

    [Theory]
    [MemberData(nameof(Providers))]
    public void A_root_written_with_or_without_a_trailing_slash_composes_identically(IStorageProviderTeamNamespace provider, string _) =>
        // The operator types the root by hand. A trailing slash is not a different destination, and treating it as one
        // would give the same deployment two namespaces for one intent.
        provider.ComposeTeamNamespace("codespace/", "team-a").ShouldBe(provider.ComposeTeamNamespace("codespace", "team-a"));

    [Fact]
    public void An_object_storage_namespace_is_a_key_prefix_its_own_schema_admits() =>
        // ConfigSchema requires a key prefix to end in a slash; a join that produced anything else would be refused by
        // the profile service at materialization rather than by anything closer to the operator.
        new AliyunOssStorageProviderModule().ComposeTeamNamespace("codespace", "team-a").ShouldBe("codespace/team-a/");

    [Fact]
    public void A_filesystem_namespace_is_a_directory_beneath_the_root() =>
        new LocalRwxStorageProviderModule().ComposeTeamNamespace("/srv/artifacts", "team-a").ShouldBe("/srv/artifacts/team-a");

    [Theory]
    [MemberData(nameof(Providers))]
    public void The_composed_namespace_changes_the_fingerprint_the_reaper_reads(IStorageProviderTeamNamespace provider, string property)
    {
        // The join is only worth anything if it reaches namespace_fingerprint, which is what the routed purge probe
        // compares to decide whether two object keys are one object.
        var module = (IStorageProviderModule)provider;

        var first = StorageProfileRules.NamespaceFingerprint(module.TypeKey, Namespace(property, provider.ComposeTeamNamespace("codespace", "team-a")));
        var second = StorageProfileRules.NamespaceFingerprint(module.TypeKey, Namespace(property, provider.ComposeTeamNamespace("codespace", "team-b")));

        first.ShouldNotBe(second);
    }

    private static System.Text.Json.JsonElement Namespace(string property, string value) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [property] = value })).RootElement.Clone();
}
