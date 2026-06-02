using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// Pins GitLabRepositoryProvider.ParseProjectAccessLevel — the pure read of the caller's effective
/// access level from GitLab's GET /projects/:id body (max of project_access + group_access). It feeds
/// the pre-flight that refuses a chat reviewer below Developer (30) BEFORE the wait resolves.
/// </summary>
[Trait("Category", "Unit")]
public class GitLabProjectAccessTests
{
    [Fact]
    public void Reads_project_access_level()
    {
        var json = """{"id":1,"permissions":{"project_access":{"access_level":30,"notification_level":3},"group_access":null}}""";

        GitLabRepositoryProvider.ParseProjectAccessLevel(json).ShouldBe(30);
    }

    [Fact]
    public void Takes_the_max_of_project_and_group_access()
    {
        var json = """{"permissions":{"project_access":{"access_level":20},"group_access":{"access_level":40}}}""";

        GitLabRepositoryProvider.ParseProjectAccessLevel(json).ShouldBe(40);
    }

    [Fact]
    public void Reporter_level_is_below_developer()
    {
        var json = """{"permissions":{"project_access":{"access_level":20},"group_access":null}}""";

        GitLabRepositoryProvider.ParseProjectAccessLevel(json).ShouldBe(20);
    }

    [Theory]
    [InlineData("""{"id":1,"name":"p"}""")]                                  // no permissions block (visible project, no grant)
    [InlineData("""{"permissions":{"project_access":null,"group_access":null}}""")]  // both null → no membership grant
    [InlineData("""{"permissions":{}}""")]                                    // empty permissions
    [InlineData("not json")]                                                  // malformed
    [InlineData("")]                                                          // empty body
    public void Returns_null_when_no_membership_grant_or_unparseable(string json)
    {
        GitLabRepositoryProvider.ParseProjectAccessLevel(json).ShouldBeNull();
    }
}
