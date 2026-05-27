using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// Test-only helper to seed a Default project for a team. Tests that exercise
/// project-scoped reads (variables, repository listings filtered by project,
/// project-detail summaries) need a Project row to assert against; this helper
/// keeps that seed consistent across tests.
///
/// <para>Live code paths handle this automatically: migration 0025 seeds Default
/// for every existing team, and <c>RepositoryBindingService.ResolveProjectAsync</c>
/// lazy-creates via <c>IProjectService.EnsureDefaultProjectAsync</c> when an
/// operator binds into a team that doesn't have one. Tests that bypass the
/// binding service (because they're testing other concerns) use this helper.</para>
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
