using System.Text.Json;
using CodeSpace.Core.Services.Workflows.RunSources.Matchers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Every repository an activation config NAMES — the input the save-time tenancy check validates against the
/// team. It must read both shapes the matchers honour (the PR triggers' <c>repositories[]</c> and the legacy /
/// push top-level <c>repositoryId</c>): a reader that missed one would let that shape name another tenant's
/// repository unchallenged. Values no matcher can match on (not a Guid) are not repository references.
/// </summary>
[Trait("Category", "Unit")]
public class ActivationRepositoryReferencesTests
{
    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{ "repositories": [] }""")]
    [InlineData("""{ "repositories": "oops" }""")]
    [InlineData("""{ "repositoryId": null }""")]
    [InlineData("""{ "repositoryId": "not-a-guid" }""")]
    [InlineData("""{ "repositoryId": 42 }""")]
    [InlineData("""{ "repositories": [{ "labels": ["bug"] }, "x", { "repositoryId": "nope" }] }""")]
    [InlineData("""{ "branches": ["main"] }""")]
    public void A_config_naming_no_repository_reads_empty(string configJson)
    {
        ActivationRepositoryReferences.Read(Parse(configJson)).ShouldBeEmpty();
    }

    [Fact]
    public void The_pr_triggers_repositories_list_is_read()
    {
        var config = Parse($$"""{ "repositories": [{ "repositoryId": "{{First}}" }, { "repositoryId": "{{Second}}", "labels": ["bug"] }] }""");

        ActivationRepositoryReferences.Read(config).ShouldBe(new[] { First, Second });
    }

    [Fact]
    public void The_legacy_and_push_top_level_repository_id_is_read()
    {
        ActivationRepositoryReferences.Read(Parse($$"""{ "repositoryId": "{{First}}", "branches": ["main"] }""")).ShouldBe(new[] { First });
    }

    [Fact]
    public void Both_shapes_together_are_read_once_each()
    {
        var config = Parse($$"""{ "repositoryId": "{{First}}", "repositories": [{ "repositoryId": "{{First}}" }, { "repositoryId": "{{Second}}" }] }""");

        ActivationRepositoryReferences.Read(config).ShouldBe(new[] { First, Second });
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
