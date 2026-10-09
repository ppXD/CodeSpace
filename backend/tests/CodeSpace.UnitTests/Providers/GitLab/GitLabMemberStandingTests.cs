using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// A GitLab merge request's author standing, from <c>GET /projects/:id/members/all/:user_id</c>. Developer is the floor:
/// it is the first role that can push to the project, so it is the GitLab counterpart of GitHub's collaborator; a Guest
/// or Reporter can see the project but holds no say over its code.
/// </summary>
[Trait("Category", "Unit")]
public class GitLabMemberStandingTests
{
    [Theory]
    [InlineData(50, PullRequestAuthorAssociation.Member)]
    [InlineData(40, PullRequestAuthorAssociation.Member)]
    [InlineData(30, PullRequestAuthorAssociation.Member)]
    [InlineData(20, PullRequestAuthorAssociation.None)]
    [InlineData(10, PullRequestAuthorAssociation.None)]
    [InlineData(5, PullRequestAuthorAssociation.None)]
    [InlineData(null, PullRequestAuthorAssociation.None)]
    public void Every_access_level_maps_to_one_standing(int? accessLevel, PullRequestAuthorAssociation expected)
    {
        GitLabRepositoryProvider.MapMemberStanding(accessLevel).ShouldBe(expected);
    }

    [Theory]
    [InlineData("""{"id":51,"username":"dev","access_level":30,"state":"active"}""", 30)]
    [InlineData("""{"id":51,"username":"dev"}""", null)]
    [InlineData("""{"access_level":"30"}""", null)]
    [InlineData("not json", null)]
    public void The_access_level_is_read_from_the_member_body(string json, int? expected)
    {
        GitLabRepositoryProvider.ParseMemberAccessLevel(json).ShouldBe(expected);
    }
}
