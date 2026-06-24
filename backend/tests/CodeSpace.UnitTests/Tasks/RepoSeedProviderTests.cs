using CodeSpace.Core.Services.Tasks.Launch.Providers.Repo;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// The repo seed provider — maps the operator's task text to the seed's goal scoped to a whole repository, and
/// requires BOTH a non-blank text (the work to do) and a target repository (the scope), since a repo surface has
/// no source entity to derive either from.
/// </summary>
[Trait("Category", "Unit")]
public class RepoSeedProviderTests
{
    [Fact]
    public async Task Maps_task_text_to_goal_scoped_to_the_target_repository()
    {
        var teamId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var seed = await new RepoSeedProvider().SeedAsync(new TaskLaunchRequest
        {
            TeamId = teamId,
            ActorUserId = Guid.NewGuid(),
            SurfaceKind = TaskLaunchSurfaceKinds.Repo,
            TaskText = "  Add structured logging across the service  ",
            RepositoryId = repoId,
            BaseBranch = "main",
        }, CancellationToken.None);

        seed.Goal.ShouldBe("Add structured logging across the service", "the goal is the trimmed task text");
        seed.SurfaceKind.ShouldBe(TaskLaunchSurfaceKinds.Repo);
        seed.TeamId.ShouldBe(teamId);
        seed.RepositoryId.ShouldBe(repoId, "the selected repository is the seed's scope");
        seed.BaseBranch.ShouldBe("main");
        seed.LinkedEntity.ShouldBeNull("a repo-wide task has no single source entity");
        seed.SeedFacts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_task_text(string? taskText)
    {
        var act = () => new RepoSeedProvider().SeedAsync(new TaskLaunchRequest
        {
            TeamId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
            SurfaceKind = TaskLaunchSurfaceKinds.Repo,
            TaskText = taskText,
            RepositoryId = Guid.NewGuid(),
        }, CancellationToken.None);

        (await Should.ThrowAsync<InvalidOperationException>(act)).Message.ShouldContain("non-blank");
    }

    [Fact]
    public async Task Rejects_a_missing_target_repository()
    {
        var act = () => new RepoSeedProvider().SeedAsync(new TaskLaunchRequest
        {
            TeamId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
            SurfaceKind = TaskLaunchSurfaceKinds.Repo,
            TaskText = "Add structured logging",
            RepositoryId = null,
        }, CancellationToken.None);

        (await Should.ThrowAsync<InvalidOperationException>(act)).Message.ShouldContain("target repository");
    }

    [Fact]
    public void Self_registers_for_the_repo_surface_kind()
    {
        new RepoSeedProvider().SurfaceKind.ShouldBe(TaskLaunchSurfaceKinds.Repo);
    }
}
