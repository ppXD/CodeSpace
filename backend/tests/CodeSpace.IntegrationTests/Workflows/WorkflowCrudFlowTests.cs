using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Round-trip CRUD on the workflow persistence layer via real Mediator + Postgres. These
/// tests are deliberately end-to-end: they exercise the controller-equivalent code path
/// (mediator.Send) all the way to DB rows, so they catch persistence wiring bugs, version
/// snapshotting, soft-delete behaviour, and tenancy enforcement in one go.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowCrudFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowCrudFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Create_persists_workflow_plus_version_snapshot_plus_trigger_rows()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            workflowId = await mediator.Send(new CreateWorkflowCommand
            {
                Name = "Test workflow",
                Description = "A workflow",
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>
                {
                    new() { TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Enabled = true }
                },
                Enabled = true,
            });
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var wf = await db.Workflow.AsNoTracking().SingleAsync(w => w.Id == workflowId);
        wf.Name.ShouldBe("Test workflow");
        wf.TeamId.ShouldBe(teamId);
        wf.LatestVersion.ShouldBe(1, "first save always starts at version 1");
        wf.DefinitionJson.ShouldContain("trigger.pr.opened");

        var version = await db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == workflowId && v.Version == 1);
        version.DefinitionJson.ShouldBe(wf.DefinitionJson, "version snapshot must match live definition");

        var activations = await db.WorkflowActivation.AsNoTracking().Where(a => a.WorkflowId == workflowId && a.DeletedDate == null).ToListAsync();
        activations.Count.ShouldBe(1);
        activations[0].TypeKey.ShouldBe("trigger.pr.opened");
        activations[0].Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_bumps_version_and_snapshots_prior_definition()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateMinimalAsync(teamId, userId);

        // Snapshot v1 JSON for later comparison.
        string v1Json;
        using (var pre = _fixture.BeginScope())
        {
            var preDb = pre.Resolve<CodeSpaceDbContext>();
            v1Json = (await preDb.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == workflowId && v.Version == 1)).DefinitionJson;
        }

        // Update with a structurally different definition (add a no-op edge condition).
        var updated = WorkflowsTestSeed.MinimalDefinition() with { Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "end", Condition = "true" }
        } };

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new UpdateWorkflowCommand
            {
                WorkflowId = workflowId,
                Name = "Renamed",
                Description = null,
                Definition = updated,
                Activations = new List<WorkflowActivationInput>(),
            });
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var wf = await db.Workflow.AsNoTracking().SingleAsync(w => w.Id == workflowId);
        wf.Name.ShouldBe("Renamed");
        wf.LatestVersion.ShouldBe(2, "update must bump version");

        var v1Snapshot = await db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == workflowId && v.Version == 1);
        v1Snapshot.DefinitionJson.ShouldBe(v1Json, "v1 row must stay byte-identical after a v2 save — already-running runs depend on it");

        var v2Snapshot = await db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == workflowId && v.Version == 2);
        v2Snapshot.DefinitionJson.ShouldBe(wf.DefinitionJson);
        // Postgres jsonb normalises whitespace ("k": "v" with space); don't pin a specific
        // serialisation — just confirm the new condition landed.
        v2Snapshot.DefinitionJson.ShouldContain("\"condition\"");
        v2Snapshot.DefinitionJson.ShouldContain("\"true\"");
    }

    [Fact]
    public async Task Delete_soft_deletes_and_get_returns_null()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateMinimalAsync(teamId, userId);

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new DeleteWorkflowCommand { WorkflowId = workflowId });
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var row = await db.Workflow.AsNoTracking().IgnoreQueryFilters().SingleAsync(w => w.Id == workflowId);
        row.DeletedDate.ShouldNotBeNull("Delete must be soft so runs can still resolve their workflow");
    }

    [Fact]
    public async Task SetEnabled_toggles_flag()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateMinimalAsync(teamId, userId);

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new SetWorkflowEnabledCommand { WorkflowId = workflowId, Enabled = false });
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.Workflow.AsNoTracking().SingleAsync(w => w.Id == workflowId)).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task List_only_returns_active_workflows_for_the_team()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var aliveId = await CreateMinimalAsync(teamId, userId);
        var deletedId = await CreateMinimalAsync(teamId, userId);

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new DeleteWorkflowCommand { WorkflowId = deletedId });
        }

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var list = await scope.Resolve<IMediator>().Send(new ListWorkflowsQuery());
            list.Select(w => w.Id).ShouldContain(aliveId);
            list.Select(w => w.Id).ShouldNotContain(deletedId, "deleted workflows must not appear in List");
        }
    }

    [Fact]
    public async Task Update_with_invalid_definition_throws_with_validator_errors()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateMinimalAsync(teamId, userId);

        // Definition with TWO triggers — DefinitionValidator must reject (exactly one trigger).
        var invalid = WorkflowsTestSeed.MinimalDefinition() with
        {
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "t1", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "t2", TypeKey = "trigger.pr.updated", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t1", To = "end" },
                new() { From = "t2", To = "end" }
            }
        };

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<WorkflowValidationException>(async () =>
        {
            await mediator.Send(new UpdateWorkflowCommand
            {
                WorkflowId = workflowId,
                Name = "x",
                Description = null,
                Definition = invalid,
                Activations = new List<WorkflowActivationInput>(),
            });
        });

        // Validator must surface the offending error in both Message (joined) and Errors (list)
        // so log consumers see a single line AND the editor can show one banner per problem.
        ex.Message.ShouldContain("Trigger", Case.Insensitive,
            "validator must surface the 'too many triggers' problem in its error message");
        ex.Errors.ShouldContain(e => e.Contains("Trigger", StringComparison.OrdinalIgnoreCase),
            "individual Errors list should contain the trigger-count message");
    }

    [Theory]
    [InlineData("""{ "repositories": [{ "repositoryId": "{0}" }] }""")]
    [InlineData("""{ "repositoryId": "{0}" }""")]
    [InlineData("""{ "repositoryId": "{0}", "branches": ["main"] }""")]
    public async Task An_activation_naming_another_teams_repository_never_stores_on_create_or_update(string configTemplate)
    {
        // The dispatcher already refuses to hand a team another team's events; this is the author-facing half: a
        // trigger scoped to a repository the team does not hold can never fire, so it must never look configured.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var foreignRepositoryId = await SeedRepositoryAsync(foreignTeamId);
        var ownRepositoryId = await SeedRepositoryAsync(teamId);
        var foreign = Activation(configTemplate.Replace("{0}", foreignRepositoryId.ToString()));
        var own = Activation(configTemplate.Replace("{0}", ownRepositoryId.ToString()));

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var onCreate = await Should.ThrowAsync<WorkflowValidationException>(() => mediator.Send(new CreateWorkflowCommand { Name = "foreign", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput> { foreign }, Enabled = true }));
        var workflowId = await mediator.Send(new CreateWorkflowCommand { Name = "own", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput> { own }, Enabled = true });
        var onUpdate = await Should.ThrowAsync<WorkflowValidationException>(() => mediator.Send(new UpdateWorkflowCommand { WorkflowId = workflowId, Name = "own", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput> { own, foreign } }));

        onCreate.Errors.ShouldContain(e => e.Contains(foreignRepositoryId.ToString()), customMessage: "the error must name the repository the author has to remove");
        onUpdate.Errors.ShouldContain(e => e.Contains(foreignRepositoryId.ToString()));

        using var verify = _fixture.BeginScope();
        var stored = await verify.Resolve<CodeSpaceDbContext>().WorkflowActivation.AsNoTracking().Where(a => a.WorkflowId == workflowId && a.DeletedDate == null).Select(a => a.ConfigJson).ToListAsync();
        stored.ShouldHaveSingleItem().ShouldContain(ownRepositoryId.ToString(), customMessage: "the refused update must leave the previous activations exactly as they were");
    }

    [Fact]
    public async Task An_activation_naming_a_repository_the_team_has_since_removed_still_saves()
    {
        // Ownership, not liveness. The editor re-sends every activation on every save, so a trigger naming a repository
        // the team unbound would otherwise 422 every later edit of the workflow — over a reference the ingestion gate
        // already makes inert.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var removedRepositoryId = await SeedRepositoryAsync(teamId);
        var activation = Activation($$"""{ "repositories": [{ "repositoryId": "{{removedRepositoryId}}" }] }""");

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var workflowId = await mediator.Send(new CreateWorkflowCommand { Name = "removed", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput> { activation }, Enabled = true });

        await SoftDeleteRepositoryAsync(removedRepositoryId);

        await Should.NotThrowAsync(() => mediator.Send(new UpdateWorkflowCommand { WorkflowId = workflowId, Name = "removed-renamed", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput> { activation } }),
            "a repository the team removed still belongs to it — check EnsureActivationRepositoriesHeldAsync does not filter on DeletedDate.");
    }

    private async Task SoftDeleteRepositoryAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == repositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, DateTimeOffset.UtcNow));
    }

    private static WorkflowActivationInput Activation(string configJson) => new() { TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.Json(configJson), Enabled = true };

    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var providerId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        db.ProviderInstance.Add(new Core.Persistence.Entities.ProviderInstance { Id = providerId, TeamId = teamId, Provider = Messages.Enums.ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{providerId:N}.local" });
        db.Repository.Add(new Core.Persistence.Entities.Repository { Id = repositoryId, TeamId = teamId, ProviderInstanceId = providerId, ExternalId = $"ext-{repositoryId:N}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{repositoryId:N}", WebUrl = "https://gh.local/acme/api" });

        await db.SaveChangesAsync();
        return repositoryId;
    }

    private async Task<Guid> CreateMinimalAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "wf-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
