using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Authorization;
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
/// Phase 3.0 — Project entity end-to-end coverage through the mediator + DB.
///
/// <para>Project is a first-class entity that owns Repositories and namespaces project-
/// scoped variables (addressable as <c>project.{slug}.{var_name}</c>). This suite proves
/// the contract operators care about: name → slug derivation, slug uniqueness per team,
/// cross-team isolation, project-variable CRUD round-trip, and cascade-soft-delete of
/// variables when the parent project is deleted.</para>
///
/// <para>Coverage:</para>
/// <list type="number">
///   <item>Create → Get round-trip (verifies slug is derived from name, not user-supplied)</item>
///   <item>Same-name collisions: second create with the same name (derived slug) is refused</item>
///   <item>Name → empty-slug rejection (theory: punctuation-only / whitespace / empty)</item>
///   <item>Cross-team isolation (team B's caller can't read team A's project)</item>
///   <item>Project-variable round-trip via the new <c>ProjectVariablesController</c> mediator surface</item>
///   <item>DeleteProject cascade-soft-deletes the project's variables</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProjectFlowTests
{
    private readonly PostgresFixture _fixture;

    public ProjectFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Create_then_Get_round_trips_project_with_derived_slug()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            projectId = await scope.Resolve<IMediator>().Send(new CreateProjectCommand
            {
                Name = "Backend Services",
                Description = "Server-side APIs",
            });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var loaded = await verify.Resolve<IMediator>().Send(new GetProjectQuery { ProjectId = projectId });

        loaded.ShouldNotBeNull();
        loaded!.Name.ShouldBe("Backend Services");
        loaded.Description.ShouldBe("Server-side APIs");
        loaded.TeamId.ShouldBe(teamId);
        loaded.Slug.ShouldBe("backend-services",
            customMessage: "ProjectService.SlugifyName derives the slug from the name — operators never type identifiers directly. " +
                           "If this changes, the wire contract for variable paths (project.{slug}.X) changes too, and every saved workflow needs an audit");
    }

    [Fact]
    public async Task Create_with_same_name_in_same_team_is_refused_with_actionable_error()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new CreateProjectCommand { Name = "Mobile" });

        // Second create with the same name → same derived slug → uq_project_team_slug_active fires.
        // Per ProjectService.CreateAsync, that's translated to InvalidOperationException with a
        // "Pick a different project name" message naming both the slug AND the original name.
        var act = async () => await mediator.Send(new CreateProjectCommand { Name = "Mobile" });
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("mobile", Case.Insensitive,
            customMessage: "the friendly error must name the derived slug so the operator knows what's already taken");
        ex.Message.ShouldContain("Mobile",
            customMessage: "the friendly error must name the original input name so the operator knows which create attempt collided");
    }

    [Theory]
    [InlineData("$$$")]       // all-punctuation → SlugifyName returns empty → InvalidOperationException
    [InlineData("...")]       // dots-only → all chars treated as non-alphanumeric → empty slug
    [InlineData("- - -")]     // hyphens+spaces → trimmed to empty
    public async Task Create_rejects_name_that_produces_empty_slug(string name)
    {
        var (teamId, userId) = await SeedTeamAsync();
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        // The static validator (ArgumentException for null/empty/whitespace) runs first;
        // names that have characters but no [A-Za-z0-9_] characters land in the
        // "no characters that produce a valid slug" branch (InvalidOperationException).
        var act = async () => await mediator.Send(new CreateProjectCommand { Name = name });
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("valid slug", Case.Insensitive,
            customMessage: "operator needs guidance that the issue is slug derivability, not raw 'invalid input' — they need to pick a name with at least one letter/digit");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_blank_name(string blankName)
    {
        var (teamId, userId) = await SeedTeamAsync();
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        // Blank/whitespace name short-circuits BEFORE slug derivation (separate error path).
        var act = async () => await mediator.Send(new CreateProjectCommand { Name = blankName });
        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Get_returns_null_for_project_owned_by_another_team()
    {
        // Two independent teams, each with its own owner-member. Create a project in team A;
        // then query for it from team B's authorized scope. Even though team B's user IS a
        // legitimate caller (passes the tenancy pipeline), the project belongs to a different
        // team_id and IProjectService.GetAsync MUST filter by team_id — anything else is a
        // cross-team data leak.
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();

        Guid projectInA;
        using (var scope = _fixture.BeginScopeAs(userA, teamA, Roles.Admin))
        {
            projectInA = await scope.Resolve<IMediator>().Send(new CreateProjectCommand { Name = "BackendA" });
        }

        using var teamBScope = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var result = await teamBScope.Resolve<IMediator>().Send(new GetProjectQuery { ProjectId = projectInA });

        result.ShouldBeNull(
            customMessage: "Team B's caller MUST NOT see a project that belongs to Team A. " +
                           "Returning the row would be a cross-team data leak — GetAsync must filter by team_id even when given a valid project id from a different tenant");
    }

    [Fact]
    public async Task Project_variable_round_trips_through_mediator_surface()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Name = "Frontend" });

            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "API_BASE",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"https://api.example.com\"").RootElement,
                Description = "Frontend's API base URL",
            });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var vars = await verify.Resolve<IMediator>().Send(new ListProjectVariablesQuery { ProjectId = projectId });

        vars.Count.ShouldBe(1);
        var v = vars[0];
        v.Name.ShouldBe("API_BASE");
        v.Scope.ShouldBe(VariableScope.Project,
            customMessage: "Variable.Scope must be Project for rows created via SetProjectVariableCommand — the polymorphic table's scope discriminator is what the resolver dispatches on");
        v.ScopeId.ShouldBe(projectId,
            customMessage: "ScopeId is the project id — VariableResolver joins this against the team's project table to resolve project.{slug}.X paths");
        v.TeamId.ShouldBe(teamId, "TeamId is denormalized for fast tenant-scoped queries");
        v.ValueType.ShouldBe(VariableValueType.String);
        v.ValuePlain.ShouldBe("\"https://api.example.com\"",
            customMessage: "non-secret values are stored as JSON-encoded text — the value is the literal serialised JsonElement, including its quotes");
        v.Description.ShouldBe("Frontend's API base URL");
    }

    [Fact]
    public async Task Delete_project_cascade_soft_deletes_its_variables()
    {
        // The Phase 3.0 contract: deleting a project soft-deletes the project row AND every
        // project-scoped variable that belongs to it. Without the cascade, re-creating a
        // project with the same slug would expose stale variable rows under the new project.
        var (teamId, userId) = await SeedTeamAsync();

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Name = "Internal" });
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "X",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"val\"").RootElement,
            });
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "Y",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"val2\"").RootElement,
            });
        }

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new DeleteProjectCommand { ProjectId = projectId });
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var project = await db.Project.AsNoTracking().SingleAsync(p => p.Id == projectId);
        project.DeletedDate.ShouldNotBeNull(
            "DeleteProject is soft-delete — the row stays in the table for audit but DeletedDate must be stamped");

        var liveVarCount = await db.Variable.AsNoTracking()
            .CountAsync(v => v.Scope == VariableScope.Project && v.ScopeId == projectId && v.DeletedDate == null);
        liveVarCount.ShouldBe(0,
            "deleting a project MUST cascade-soft-delete its project-scoped variables. " +
            "Without this, re-creating a project with the same slug (after the unique-active index allows it) would surface the old variable rows under the new project id mapping");

        // Defensive: confirm the rows STILL EXIST with DeletedDate set (audit trail) — i.e.
        // the cascade is soft, not hard. If a future change accidentally swaps to hard-delete,
        // this assertion catches it.
        var totalVarCount = await db.Variable.AsNoTracking()
            .CountAsync(v => v.Scope == VariableScope.Project && v.ScopeId == projectId);
        totalVarCount.ShouldBe(2,
            "soft-delete preserves the variable rows for audit — only DeletedDate is stamped. " +
            "A row count drop here means the cascade flipped to hard-delete, losing the audit trail");
    }

    /// <summary>
    /// Seeds a fresh team with its owner-user + a TeamMembership row so the user actually
    /// has standing to call the mediator (the tenancy pipeline checks membership, not just
    /// that the user exists). Mirrors <c>WorkflowsTestSeed.SeedTeamAsync</c> but lives here
    /// to avoid the cross-namespace test-helper dependency.
    /// </summary>
    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User
        {
            Id = userId,
            Email = $"proj-{userId:N}@test.local",
            Name = $"proj-user-{userId:N}",
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        db.Team.Add(new Team
        {
            Id = teamId,
            Slug = $"proj-team-{teamId:N}",
            Name = "Project Test Team",
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

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
