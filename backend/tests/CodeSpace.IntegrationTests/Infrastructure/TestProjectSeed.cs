using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// Test-only helper to seed a Default project for a team. Phase 3.0 made
/// <c>repository.project_id</c> NOT NULL, so any test that builds Repository
/// rows directly via EF must also seed a project for the same team and thread
/// the project id into the Repository.
///
/// <para>Live code paths handle this automatically: migration 0025 seeds Default
/// for every existing team, and <c>RepositoryBindingService.ResolveProjectAsync</c>
/// lazy-creates via <c>IProjectService.EnsureDefaultProjectAsync</c> when an
/// operator binds into a team that doesn't have one. Tests that bypass the
/// binding service (because they're testing other concerns) need this helper.</para>
/// </summary>
public static class TestProjectSeed
{
    public static Project BuildDefaultProject(Guid teamId, Guid actorUserId)
    {
        var now = DateTimeOffset.UtcNow;
        return new Project
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = "default",
            Name = "Default",
            Description = "Test default project",
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        };
    }
}
