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
[Trait("Category", "Integration")]
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
    public async Task Cross_team_project_variable_access_is_refused_for_every_verb()
    {
        // Security boundary — the most important contract on the new ProjectVariablesController:
        // Team B's caller MUST NOT be able to Set, List, or Delete variables on Team A's
        // project even though they hold a valid (cross-team) project id. IVariableService's
        // EnsureScopeBelongsToTeamAsync's Project branch enforces this via a
        // (p.Id == scopeId && p.TeamId == expectedTeamId) probe — anything that bypasses
        // that surfaces here as an unauthorised mutation.
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();

        Guid projectInA;
        using (var scope = _fixture.BeginScopeAs(userA, teamA, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectInA = await mediator.Send(new CreateProjectCommand { Name = "Secret Stuff" });
            // Seed one variable so List would actually have something to leak if it didn't enforce tenancy.
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectInA,
                Name = "API_KEY",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"team-a-secret\"").RootElement,
            });
        }

        using var attackerScope = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var attackerMediator = attackerScope.Resolve<IMediator>();

        // Verb 1 — Set. Attacker tries to upsert into Team A's project.
        var setAct = async () => await attackerMediator.Send(new SetProjectVariableCommand
        {
            ProjectId = projectInA,
            Name = "API_KEY",
            ValueType = VariableValueType.String,
            Value = JsonDocument.Parse("\"hijacked\"").RootElement,
        });
        await setAct.ShouldThrowAsync<KeyNotFoundException>(
            customMessage: "SetProjectVariableCommand from a foreign team MUST throw KeyNotFoundException (the 'not-yours-or-not-found' 404 conflation) — anything else lets one tenant write into another tenant's project variables");

        // Verb 2 — List. Attacker tries to read.
        var listAct = async () => await attackerMediator.Send(new ListProjectVariablesQuery { ProjectId = projectInA });
        await listAct.ShouldThrowAsync<KeyNotFoundException>(
            customMessage: "ListProjectVariablesQuery from a foreign team MUST throw — returning even an empty list would confirm 'this project id exists', which is itself a tenancy leak");

        // Verb 3 — Delete. Attacker tries to wipe Team A's variable.
        var delAct = async () => await attackerMediator.Send(new DeleteProjectVariableCommand
        {
            ProjectId = projectInA,
            Name = "API_KEY",
        });
        await delAct.ShouldThrowAsync<KeyNotFoundException>(
            customMessage: "DeleteProjectVariableCommand from a foreign team MUST throw — silent no-op (the normal idempotent-delete path) would let an attacker destroy another tenant's data");

        // Defensive ground-truth: the original variable in Team A is still there, untouched.
        using var verify = _fixture.BeginScopeAs(userA, teamA, Roles.Admin);
        var stillThere = await verify.Resolve<IMediator>().Send(new ListProjectVariablesQuery { ProjectId = projectInA });
        stillThere.Count.ShouldBe(1, "the attacker's verbs all threw; Team A's original variable must be unchanged");
        stillThere[0].ValuePlain.ShouldBe("\"team-a-secret\"",
            customMessage: "the Set verb threw, so the original value MUST still be 'team-a-secret' — if it changed to 'hijacked' the tenancy guard failed silently");
    }

    [Fact]
    public async Task Set_with_existing_name_replaces_value_in_place_no_duplicate_row()
    {
        // Upsert semantics: calling SetProjectVariableCommand twice with the same name
        // mutates the existing row (rotation in-place), not a second insert. Same contract
        // as Team / Workflow scopes — IVariableService.SetAsync routes both new + existing
        // tuples through the (scope, scopeId, name) lookup before deciding insert vs update.
        var (teamId, userId) = await SeedTeamAsync();

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Name = "Mutable" });
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "ROTATE_ME",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"v1\"").RootElement,
            });
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "ROTATE_ME",
                ValueType = VariableValueType.String,
                Value = JsonDocument.Parse("\"v2\"").RootElement,
                Description = "rotated",
            });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var vars = await verify.Resolve<IMediator>().Send(new ListProjectVariablesQuery { ProjectId = projectId });

        vars.Count.ShouldBe(1,
            customMessage: "second Set on the same (scope, scopeId, name) tuple MUST update in place, not insert a second row. " +
                           "Two rows here means rotation creates phantoms that the resolver could pick the wrong one of");
        vars[0].ValuePlain.ShouldBe("\"v2\"", "the second Set's value wins — most-recent-write semantics");
        vars[0].Description.ShouldBe("rotated", "description is replaced, not merged");
    }

    [Fact]
    public async Task Secret_project_variable_persists_encrypted_and_never_returns_plaintext()
    {
        // Phase 2.6/2.7 contract — secret values are AES-256-GCM encrypted in the DB and the
        // List API NEVER returns plaintext (only the metadata + valueType=Secret marker).
        // The Workflow + Team scopes were already covered by their own test suites; this
        // proves the Project-scope wiring inherits the same protection rather than
        // accidentally falling through to the plain-text path.
        var (teamId, userId) = await SeedTeamAsync();

        Guid projectId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Name = "VaultLike" });
            await mediator.Send(new SetProjectVariableCommand
            {
                ProjectId = projectId,
                Name = "ANTHROPIC_API_KEY",
                ValueType = VariableValueType.Secret,
                Value = JsonDocument.Parse("\"sk-ant-this-must-never-leak\"").RootElement,
            });
        }

        // Operator-facing read MUST return null for ValuePlain on Secret rows.
        using (var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var vars = await verify.Resolve<IMediator>().Send(new ListProjectVariablesQuery { ProjectId = projectId });
            vars.Count.ShouldBe(1);
            vars[0].ValueType.ShouldBe(VariableValueType.Secret);
            vars[0].ValuePlain.ShouldBeNull(
                customMessage: "Secret rows MUST have ValuePlain=null on the API surface. " +
                               "Any non-null value here is a critical regression: the secret just went over the wire to whoever called List");
        }

        // Ground truth at the DB layer: the plaintext column is null AND the encrypted column
        // contains bytes that don't include the plaintext literal. If the encryption layer
        // silently fell back to plain storage (a real regression mode if the configured
        // encryption key is missing), this catches it where the API surface alone wouldn't.
        using var db = _fixture.BeginScope().Resolve<CodeSpaceDbContext>();
        var row = await db.Variable.AsNoTracking()
            .SingleAsync(v => v.Scope == VariableScope.Project && v.ScopeId == projectId && v.Name == "ANTHROPIC_API_KEY");
        row.ValuePlain.ShouldBeNull("Secret rows MUST have value_plain NULL in the DB — the encrypted column is the only source of truth");
        row.ValueEncrypted.ShouldNotBeNull("Secret rows MUST have a non-null value_encrypted blob — null here means encryption silently bypassed");
        row.ValueEncrypted!.Length.ShouldBeGreaterThan(0);
        System.Text.Encoding.UTF8.GetString(row.ValueEncrypted!).ShouldNotContain("sk-ant-this-must-never-leak",
            customMessage: "the encrypted bytes MUST NOT contain the plaintext literal — if this fails, the encryption layer silently fell through to identity (key missing? AES routine throwing and the row stored the raw input?)");
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
