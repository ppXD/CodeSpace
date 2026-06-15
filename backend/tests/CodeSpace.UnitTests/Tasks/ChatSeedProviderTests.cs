using CodeSpace.Core.Services.Tasks.Launch.Providers.Chat;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// The chat seed provider — maps the operator's task text to the seed's goal, requires a non-blank text (chat
/// has no source entity to derive from), and propagates the team / repo / branch the request carried.
/// </summary>
[Trait("Category", "Unit")]
public class ChatSeedProviderTests
{
    [Fact]
    public async Task Maps_task_text_to_goal_and_carries_team_repo_branch()
    {
        var teamId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var seed = await new ChatSeedProvider().SeedAsync(new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = Guid.NewGuid(),
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = "  Refactor the auth module  ",
            RepositoryId = repoId,
            BaseBranch = "develop",
        }, CancellationToken.None);

        seed.Goal.ShouldBe("Refactor the auth module", "the goal is the trimmed task text");
        seed.SurfaceKind.ShouldBe(TaskLaunchSurfaceKinds.Chat);
        seed.TeamId.ShouldBe(teamId);
        seed.RepositoryId.ShouldBe(repoId);
        seed.BaseBranch.ShouldBe("develop");
        seed.LinkedEntity.ShouldBeNull("a free-text chat task has no source entity");
        seed.SuggestedEffort.ShouldBeNull();
        seed.SuggestedRecipe.ShouldBeNull();
        seed.SeedFacts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_task_text(string? taskText)
    {
        var act = () => new ChatSeedProvider().SeedAsync(new TaskLaunchRequest
        {
            TeamId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
            SurfaceKind = TaskLaunchSurfaceKinds.Chat,
            TaskText = taskText,
        }, CancellationToken.None);

        (await Should.ThrowAsync<InvalidOperationException>(act)).Message.ShouldContain("non-blank");
    }
}
