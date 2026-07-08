using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Projects;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Projects;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Projects;

/// <summary>
/// Phase 3.1 — pin the <c>project_repository</c> link table semantics through the engine
/// end-to-end. The previous schema had <c>repository.project_id</c> as a 1:N FK; this
/// suite proves the new N:M shape works correctly across the bind flow, the list-by-project
/// query, the project-detail count, MoveRepositoryAsync's "set to exactly this project"
/// semantic, and the "delete blocked when active links exist" guard.
///
/// <para>The legacy <c>repository.project_id</c> column is still dual-written during the
/// transition (see migration 0026 + RepositoryBindingService.BuildRepositoryEntity). These
/// tests don't assert anything about that column — they assert behaviour through the link
/// table only, which is the contract going forward.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProjectRepositoryLinkFlowTests
{
    private readonly PostgresFixture _fixture;
    public ProjectRepositoryLinkFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Repository_can_belong_to_multiple_projects_via_the_link_table()
    {
        // The whole point of the N:M shape — a single repository can be attached to many
        // projects. We do this through direct link-table writes here (the public bind flow
        // creates one link per call; the N:M behaviour is enforced by the schema, not by
        // the bind flow specifically).
        var (teamId, userId, repoId) = await SeedTeamWithRepoAsync().ConfigureAwait(false);

        Guid projectA, projectB;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectA = await mediator.Send(new CreateProjectCommand { Name = "Project A" });
            projectB = await mediator.Send(new CreateProjectCommand { Name = "Project B" });
        }

        var now = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ProjectRepository.Add(new ProjectRepository { ProjectId = projectA, RepositoryId = repoId, TeamId = teamId, CreatedDate = now, LastModifiedDate = now });
            db.ProjectRepository.Add(new ProjectRepository { ProjectId = projectB, RepositoryId = repoId, TeamId = teamId, CreatedDate = now, LastModifiedDate = now });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        // Both projects see the same repo active.
        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();
        var linkedProjectIds = await verifyDb.ProjectRepository.AsNoTracking()
            .Where(pr => pr.RepositoryId == repoId && pr.DeletedDate == null)
            .Select(pr => pr.ProjectId)
            .ToListAsync().ConfigureAwait(false);

        linkedProjectIds.Count.ShouldBe(2,
            customMessage: "the composite (ProjectId, RepositoryId) PK should allow the same repo to link to two distinct projects — that's the N:M shape. " +
                           "A duplicate-key violation here means the schema regressed to 1:N");
        linkedProjectIds.ShouldContain(projectA);
        linkedProjectIds.ShouldContain(projectB);
    }

    [Fact]
    public async Task ListRepositories_filtered_by_project_returns_repos_via_the_link_table()
    {
        // The list endpoint now filters via project_repository instead of the legacy
        // repository.project_id column. Pin this — a regression in the query shape would
        // surface here as "the repo I just linked doesn't appear in the project listing".
        var (teamId, userId, repoId) = await SeedTeamWithRepoAsync().ConfigureAwait(false);

        Guid otherProjectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            otherProjectId = await scope.Resolve<IMediator>().Send(new CreateProjectCommand { Name = "Filtered" });
        }

        // Link the existing repo to the new project.
        var now = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ProjectRepository.Add(new ProjectRepository { ProjectId = otherProjectId, RepositoryId = repoId, TeamId = teamId, CreatedDate = now, LastModifiedDate = now });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var scopeAs = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var bound = await scopeAs.Resolve<IMediator>().Send(new ListRepositoriesQuery { ProjectId = otherProjectId });

        bound.Count.ShouldBe(1,
            customMessage: "filtering by project_id MUST surface the repo we just linked. " +
                           "Zero rows means the filter is still hitting repository.project_id rather than project_repository — link table query is broken");
        bound[0].Id.ShouldBe(repoId);
        bound[0].Projects.ShouldContain(p => p.Id == otherProjectId,
            customMessage: "the response should include the project in Projects[]; a missing entry means the projection isn't joining through project_repository");
    }

    [Fact]
    public async Task GetProject_reports_active_repository_count_via_the_link_table()
    {
        var (teamId, userId, repoId) = await SeedTeamWithRepoAsync().ConfigureAwait(false);

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            projectId = await scope.Resolve<IMediator>().Send(new CreateProjectCommand { Name = "Counted" });
        }

        // Before any link — count must be 0.
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var summary = await scope.Resolve<IMediator>().Send(new GetProjectQuery { ProjectId = projectId });
            summary!.ActiveRepositoryCount.ShouldBe(0,
                customMessage: "fresh project, no repos linked → count should be 0. " +
                               "Any non-zero value means the query is still counting repository.project_id instead of project_repository, " +
                               "or the link-table query isn't filtering out the rows from other projects");
        }

        // Link one repo, count should go to 1.
        var now = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ProjectRepository.Add(new ProjectRepository { ProjectId = projectId, RepositoryId = repoId, TeamId = teamId, CreatedDate = now, LastModifiedDate = now });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var summary = await scope.Resolve<IMediator>().Send(new GetProjectQuery { ProjectId = projectId });
            summary!.ActiveRepositoryCount.ShouldBe(1);
        }
    }

    [Fact]
    public async Task DeleteProject_is_refused_when_an_active_link_exists()
    {
        var (teamId, userId, repoId) = await SeedTeamWithRepoAsync().ConfigureAwait(false);

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            projectId = await scope.Resolve<IMediator>().Send(new CreateProjectCommand { Name = "Tied" });
        }

        // Attach repo to the project.
        var now = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ProjectRepository.Add(new ProjectRepository { ProjectId = projectId, RepositoryId = repoId, TeamId = teamId, CreatedDate = now, LastModifiedDate = now });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        // Delete is now blocked — same guard as Phase 3.0, but the check is the link table.
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var act = async () => await scope.Resolve<IMediator>().Send(new DeleteProjectCommand { ProjectId = projectId });
            var ex = await act.ShouldThrowAsync<InvalidOperationException>();
            ex.Message.ShouldContain("active repositor",
                customMessage: "the refusal message names 'active repositor(y/ies)' so the operator knows what to detach. " +
                               "Pre-3.1 the count was via repository.project_id; this assertion proves the link-table count works the same way");
        }
    }

    [Fact]
    public async Task MoveRepository_atomically_detaches_existing_links_and_attaches_target()
    {
        // N:M semantic for the legacy frontend Move flow: "set repo membership to exactly
        // the target project" — atomically detach every other active link, attach target.
        var (teamId, userId, repoId) = await SeedTeamWithRepoAsync().ConfigureAwait(false);

        Guid projectA, projectB;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectA = await mediator.Send(new CreateProjectCommand { Name = "From" });
            projectB = await mediator.Send(new CreateProjectCommand { Name = "To" });
        }

        var now = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ProjectRepository.Add(new ProjectRepository { ProjectId = projectA, RepositoryId = repoId, TeamId = teamId, CreatedDate = now, LastModifiedDate = now });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new MoveRepositoryToProjectCommand
            {
                RepositoryId = repoId,
                TargetProjectId = projectB,
            });
        }

        // Active links: only projectB. The projectA link should be soft-deleted (DeletedDate set).
        using var verify = _fixture.BeginScope();
        var db2 = verify.Resolve<CodeSpaceDbContext>();
        var allLinks = await db2.ProjectRepository.AsNoTracking()
            .Where(pr => pr.RepositoryId == repoId)
            .ToListAsync().ConfigureAwait(false);

        var active = allLinks.Where(l => l.DeletedDate == null).Select(l => l.ProjectId).ToList();
        active.Count.ShouldBe(1, "after Move, exactly one active link should remain");
        active[0].ShouldBe(projectB, "active link must point at the move target");

        var detached = allLinks.SingleOrDefault(l => l.ProjectId == projectA);
        detached.ShouldNotBeNull("the original projectA link is preserved (soft-delete) — operator needs an audit trail");
        detached!.DeletedDate.ShouldNotBeNull("soft-delete sets DeletedDate; hard-delete would lose the audit row");
    }

    /// <summary>
    /// Seeds a fresh team + user + a single bound repository. Caller verifies team-scoped
    /// operations using <c>BeginScopeAs(userId, teamId, Roles.Admin)</c>.
    /// </summary>
    private async Task<(Guid TeamId, Guid UserId, Guid RepoId)> SeedTeamWithRepoAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User
        {
            Id = userId,
            Email = $"prl-{userId:N}@test.local",
            Name = $"prl-{userId:N}",
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        db.Team.Add(new Team
        {
            Id = teamId,
            Slug = $"prl-team-{teamId:N}",
            Name = "PRL Team",
            Kind = TeamKind.Workspace,
            OwnerUserId = userId,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        db.TeamMembership.Add(new TeamMembership
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamRole.Owner,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        var defaultProject = TestProjectSeed.BuildDefaultProject(teamId, userId);
        db.Project.Add(defaultProject);

        var providerInstance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.GitHub,
            DisplayName = $"prl-pi-{teamId:N}",
            BaseUrl = "https://api.github.com",
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        };
        db.ProviderInstance.Add(providerInstance);

        var repoId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        db.Repository.Add(new Repository
        {
            Id = repoId,
            TeamId = teamId,
            ProviderInstanceId = providerInstance.Id,
            ExternalId = $"prl-ext-{suffix}",
            NamespacePath = "ns",
            Name = $"r-{suffix}",
            FullPath = $"ns/r-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://x",
            Status = RepositoryStatus.Active,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
        return (teamId, userId, repoId);
    }
}
