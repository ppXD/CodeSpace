using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Projects;
using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Projects;
using CodeSpace.Messages.Queries.Variables;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Projects;

/// <summary>
/// Phase 3.0 — Project as Variable namespace. Project is a first-class entity but has no
/// FK relationship to workflow / repository / workflow_run; it exists purely to namespace
/// variables addressable as <c>project.{slug}.{var_name}</c>.
///
/// <para>Covers:</para>
/// <list type="number">
///   <item>Project CRUD round-trip via the mediator + RBAC tenancy guard</item>
///   <item>Slug uniqueness within a team (creating duplicate fails)</item>
///   <item>Slug regex rejects dots / spaces</item>
///   <item>Cross-team isolation (Team A's project invisible from Team B)</item>
///   <item>Project-scoped variable CRUD (Variable.Scope=Project, ScopeId=ProjectId)</item>
///   <item>Soft-delete cascade-soft-deletes the project's variables</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProjectFlowTests
{
    private readonly PostgresFixture _fixture;

    public ProjectFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Create_then_Get_round_trips_project_through_mediator()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand
            {
                Slug = "Backend",
                Name = "Backend Services",
                Description = "Server-side APIs",
            }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var verifyMediator = verify.Resolve<IMediator>();
        var loaded = await verifyMediator.Send(new GetProjectQuery { ProjectId = projectId }).ConfigureAwait(false);

        loaded.ShouldNotBeNull();
        loaded!.Slug.ShouldBe("Backend");
        loaded.Name.ShouldBe("Backend Services");
        loaded.Description.ShouldBe("Server-side APIs");
        loaded.TeamId.ShouldBe(teamId);
    }

    [Fact]
    public async Task Create_duplicate_slug_in_same_team_fails()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        await mediator.Send(new CreateProjectCommand { Slug = "Mobile", Name = "Mobile" }).ConfigureAwait(false);

        var act = async () => await mediator.Send(new CreateProjectCommand { Slug = "Mobile", Name = "Mobile 2" }).ConfigureAwait(false);
        await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
    }

    [Theory]
    [InlineData("with.dot")]      // dots collide with project.X.Y path syntax
    [InlineData("with space")]    // spaces broken in paths
    [InlineData("")]              // empty
    [InlineData("$pecial")]       // bad chars
    public async Task Create_rejects_invalid_slug(string slug)
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var act = async () => await mediator.Send(new CreateProjectCommand { Slug = slug, Name = "X" }).ConfigureAwait(false);
        await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Get_returns_null_for_cross_team_project()
    {
        var teamA = await SeedTeamAsync().ConfigureAwait(false);
        var teamB = await SeedTeamAsync().ConfigureAwait(false);

        Guid projectInA;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamA, Roles.Admin))
        {
            projectInA = await scope.Resolve<IMediator>().Send(new CreateProjectCommand { Slug = "BackendA", Name = "BackendA" }).ConfigureAwait(false);
        }

        using var teamBScope = _fixture.BeginScopeAs(Guid.NewGuid(), teamB, Roles.Admin);
        var result = await teamBScope.Resolve<IMediator>().Send(new GetProjectQuery { ProjectId = projectInA }).ConfigureAwait(false);

        result.ShouldBeNull(
            customMessage: "Team B must NOT see a project that belongs to Team A — cross-team isolation invariant.");
    }

    [Fact]
    public async Task Project_variables_round_trip_via_scope_polymorphism()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Slug = "Frontend", Name = "Frontend" }).ConfigureAwait(false);

            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "API_BASE",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"https://api.example.com\"").RootElement,
            }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var vars = await verify.Resolve<IMediator>().Send(new ListProjectVariablesQuery { ProjectId = projectId }).ConfigureAwait(false);

        vars.Count.ShouldBe(1);
        vars[0].Name.ShouldBe("API_BASE");
        vars[0].Scope.ShouldBe(VariableScope.Project);
        vars[0].ScopeId.ShouldBe(projectId);
        vars[0].ValuePlain.ShouldBe("\"https://api.example.com\"");
    }

    [Fact]
    public async Task Delete_project_cascade_soft_deletes_variables()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Slug = "Internal", Name = "Internal" }).ConfigureAwait(false);
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "X",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"val\"").RootElement,
            }).ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new DeleteProjectCommand { ProjectId = projectId }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var project = await db.Project.AsNoTracking().SingleAsync(p => p.Id == projectId).ConfigureAwait(false);
        project.DeletedDate.ShouldNotBeNull();

        var liveVariables = await db.Variable.AsNoTracking()
            .Where(v => v.Scope == VariableScope.Project && v.ScopeId == projectId && v.DeletedDate == null)
            .CountAsync().ConfigureAwait(false);
        liveVariables.ShouldBe(0,
            customMessage: "Deleting a Project must cascade-soft-delete its variables — otherwise re-creating the slug exposes stale variable rows.");
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team", OwnerUserId = owner.Id };

        db.User.Add(owner);
        db.Team.Add(team);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return team.Id;
    }
}
