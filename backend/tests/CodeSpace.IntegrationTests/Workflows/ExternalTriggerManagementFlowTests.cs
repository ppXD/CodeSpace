using System.Text.Json;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExternalTriggerManagementFlowTests(PostgresFixture fixture)
{
    private sealed record Seed(Guid TeamId, Guid UserId, Guid WorkflowId, Guid TriggerId, string Slug, string Key);

    [Fact]
    public async Task Metadata_returns_the_stable_trigger_identity_without_a_slug()
    {
        var seed = await SeedAsync();
        await UpdateAsync(seed, Definition(seed.Slug));
        using var read = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var result = await read.Resolve<IMediator>().Send(new ListWorkflowExternalTriggersQuery { WorkflowId = seed.WorkflowId });
        var trigger = result!.Triggers.ShouldHaveSingleItem();
        trigger.Id.ShouldBe((await RowAsync(seed)).Id);
        trigger.WorkflowId.ShouldBe(seed.WorkflowId);
        trigger.NodeId.ShouldBe("start");
        JsonSerializer.SerializeToElement(trigger, WorkflowJson.Options).TryGetProperty("slug", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Saving_a_legacy_slug_removes_it_without_changing_the_trigger_credential()
    {
        var seed = await SeedAsync();
        var before = await RowAsync(seed);
        string originalDefinition;
        string originalHash;
        using (var history = fixture.BeginScope())
        {
            var original = await history.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking()
                .SingleAsync(v => v.WorkflowId == seed.WorkflowId && v.Version == 1);
            originalDefinition = original.DefinitionJson;
            originalHash = original.DefinitionHash;
        }
        await UpdateAsync(seed, Definition("legacy-name"));
        var after = await RowAsync(seed);
        after.Id.ShouldBe(before.Id);
        after.KeyDigest.ShouldBe(before.KeyDigest);
        after.KeyCiphertext.ShouldBe(before.KeyCiphertext);
        using var scope = fixture.BeginScope();
        var version = await scope.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking()
            .SingleAsync(v => v.WorkflowId == seed.WorkflowId && v.Version == 2);
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(version.DefinitionJson, WorkflowJson.Options)!;
        definition.Nodes.Single(n => n.Id == "start").Config.TryGetProperty("slug", out _).ShouldBeFalse();
        version.DefinitionHash.ShouldBe(DefinitionHash.Compute(definition));
        var preserved = await scope.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking()
            .SingleAsync(v => v.WorkflowId == seed.WorkflowId && v.Version == 1);
        preserved.DefinitionJson.ShouldBe(originalDefinition);
        preserved.DefinitionHash.ShouldBe(originalHash);
    }

    [Fact]
    public async Task Metadata_does_not_return_another_teams_workflow()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        using var scope = fixture.BeginScopeAs(other.UserId, other.TeamId);
        (await scope.Resolve<IMediator>().Send(new ListWorkflowExternalTriggersQuery { WorkflowId = seed.WorkflowId })).ShouldBeNull();
        var own = await scope.Resolve<IMediator>().Send(new ListWorkflowExternalTriggersQuery { WorkflowId = other.WorkflowId });
        own!.Triggers.ShouldHaveSingleItem().WorkflowId.ShouldBe(other.WorkflowId);
    }

    [Fact]
    public async Task Metadata_is_empty_for_a_workflow_that_never_had_an_external_trigger()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var workflowId = await CreateAsync(scope, WorkflowsTestSeed.MinimalDefinition());
        var result = await scope.Resolve<IMediator>().Send(new ListWorkflowExternalTriggersQuery { WorkflowId = workflowId });
        result.ShouldNotBeNull();
        result.Triggers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Metadata_lists_every_live_row_without_assuming_a_single_trigger()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        await scope.Resolve<IExternalTriggerStore>().EnsureForNodeAsync(seed.WorkflowId, seed.TeamId,
            "second", seed.UserId, CancellationToken.None);
        var result = await scope.Resolve<IMediator>().Send(new ListWorkflowExternalTriggersQuery { WorkflowId = seed.WorkflowId });
        result!.Triggers.Select(t => t.NodeId).Order().ShouldBe(new[] { "second", "start" });
        result.Triggers.ShouldAllBe(t => t.WorkflowId == seed.WorkflowId);
    }

    [Fact]
    public async Task Metadata_excludes_retired_nodes_and_deleted_workflows()
    {
        var seed = await SeedAsync();
        using (var setup = fixture.BeginScope())
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            await db.WorkflowExternalTrigger.Where(t => t.WorkflowId == seed.WorkflowId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedDate, DateTimeOffset.UtcNow));
        }
        using (var read = fixture.BeginScopeAs(seed.UserId, seed.TeamId))
        {
            var result = await read.Resolve<IMediator>().Send(new ListWorkflowExternalTriggersQuery { WorkflowId = seed.WorkflowId });
            result!.Triggers.ShouldBeEmpty();
        }
        using (var setup = fixture.BeginScope())
            await setup.Resolve<CodeSpaceDbContext>().Workflow.Where(w => w.Id == seed.WorkflowId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.DeletedDate, DateTimeOffset.UtcNow));
        using var verify = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        (await verify.Resolve<IMediator>().Send(new ListWorkflowExternalTriggersQuery { WorkflowId = seed.WorkflowId })).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_projects_the_node_contract_into_the_hashed_version_inputs(bool update)
    {
        var seed = await SeedAsync();
        if (update)
        {
            var definition = Definition(seed.Slug) with
            {
                Inputs = [new WorkflowVariable { Name = "stale", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") }],
            };
            definition = definition with
            {
                Nodes = definition.Nodes.Select(n => n.Id == "start" ? n with { Config = Config(seed.Slug, "integer") } : n).ToArray(),
            };
            await UpdateAsync(seed, definition);
        }

        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var version = await db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == seed.WorkflowId && v.Version == (update ? 2 : 1));
        using var json = JsonDocument.Parse(version.DefinitionJson);
        var inputs = json.RootElement.GetProperty("inputs");
        inputs.GetArrayLength().ShouldBe(1);
        inputs[0].GetProperty("name").GetString().ShouldBe("service");
        inputs[0].GetProperty("required").GetBoolean().ShouldBeTrue();
        inputs[0].GetProperty("schema").GetProperty("type").GetString().ShouldBe(update ? "integer" : "string");
        var saved = JsonSerializer.Deserialize<WorkflowDefinition>(version.DefinitionJson, WorkflowJson.Options)!;
        version.DefinitionHash.ShouldBe(DefinitionHash.Compute(saved));
        var workflow = await db.Workflow.AsNoTracking().SingleAsync(w => w.Id == seed.WorkflowId);
        workflow.DefinitionJson.ShouldBe(version.DefinitionJson);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("position")]
    [InlineData("schema")]
    [InlineData("slug")]
    public async Task Saving_the_workflow_again_leaves_the_key_and_endpoint_working(string edit)
    {
        var seed = await SeedAsync();
        var before = await RowAsync(seed);
        var definition = Definition(seed.Slug);
        definition = definition with
        {
            Nodes = definition.Nodes.Select(n => edit switch
            {
                "label" when n.Id == "end" => n with { Label = "Renamed" },
                "position" when n.Id == "start" => n with { Position = new NodePosition { X = 400, Y = 250 } },
                "schema" when n.Id == "start" => n with { Config = Config(seed.Slug, "integer") },
                "slug" when n.Id == "start" => n with { Config = Config("different-draft") },
                _ => n,
            }).ToArray(),
        };
        await UpdateAsync(seed, definition);
        var after = await RowAsync(seed);
        after.Id.ShouldBe(before.Id);
        after.KeyDigest.ShouldBe(before.KeyDigest);
        after.KeyCiphertext.ShouldBe(before.KeyCiphertext);
        (await ReceiveAsync(seed, body: edit == "schema" ? "{\"service\":7}" : "{\"service\":\"api\"}")).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        if (edit == "schema")
            (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.InvalidPayload);
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var activations = await db.WorkflowActivation.Where(a => a.WorkflowId == seed.WorkflowId).ToArrayAsync();
        activations.Count(a => a.DeletedDate == null).ShouldBe(1);
        activations.Count(a => a.DeletedDate != null).ShouldBe(1);
        var run = await db.WorkflowRun.Include(r => r.RunRequest).SingleAsync(r => r.WorkflowId == seed.WorkflowId);
        run.RunRequest.ActivationId.ShouldBe(activations.Single(a => a.DeletedDate == null).Id);
    }

    [Fact]
    public async Task A_legacy_name_does_not_affect_the_live_endpoint()
    {
        var seed = await SeedAsync();
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        var before = await RowAsync(seed);

        await UpdateAsync(seed, Definition("告警"));

        var after = await RowAsync(seed);
        after.Id.ShouldBe(before.Id);
        after.KeyDigest.ShouldBe(before.KeyDigest);
        after.KeyCiphertext.ShouldBe(before.KeyCiphertext);
        var outcome = await ReceiveAsync(seed);
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        outcome.RunId.ShouldNotBe(Guid.Empty);
        using var scope = fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.SingleAsync(r => r.Id == outcome.RunId);
        run.WorkflowId.ShouldBe(seed.WorkflowId);

    }

    [Fact]
    public async Task Reveal_hands_back_the_minted_key()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        (await scope.Resolve<IMediator>().Send(new RevealExternalTriggerKeyCommand { TriggerId = seed.TriggerId })).ShouldBe(seed.Key);
    }

    [Theory]
    [InlineData(TeamRole.Viewer, "reveal")]
    [InlineData(TeamRole.Viewer, "rotate")]
    [InlineData(TeamRole.Member, "reveal")]
    [InlineData(TeamRole.Member, "rotate")]
    [InlineData(TeamRole.Admin, "reveal")]
    public async Task Management_requires_workflows_write(TeamRole role, string operation)
    {
        var seed = await SeedAsync();
        using (var setup = fixture.BeginScope())
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            (await db.TeamMembership.SingleAsync(m => m.TeamId == seed.TeamId)).Role = role;
            await db.SaveChangesAsync();
        }
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var error = await Record.ExceptionAsync(() => ManageAsync(scope.Resolve<IMediator>(), operation, seed.TriggerId));
        if (role == TeamRole.Viewer) error.ShouldBeOfType<TenantAccessDeniedException>();
        else error.ShouldBeNull();
    }

    [Fact]
    public async Task Rotating_invalidates_the_old_key_immediately_and_reveal_returns_the_new_key()
    {
        var seed = await SeedAsync();
        string fresh;
        using (var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId))
            fresh = await scope.Resolve<IMediator>().Send(new RotateExternalTriggerKeyCommand { TriggerId = seed.TriggerId });
        fresh.ShouldNotBe(seed.Key);
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
        (await ReceiveAsync(seed with { Key = fresh })).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        await UpdateAsync(seed, Definition(seed.Slug));
        using var verify = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        (await verify.Resolve<IMediator>().Send(new RevealExternalTriggerKeyCommand { TriggerId = seed.TriggerId })).ShouldBe(fresh);
    }

    [Fact]
    public async Task Rotation_waits_for_workflow_retirement_and_cannot_restore_the_retired_key()
    {
        var seed = await SeedAsync();
        using var retirement = fixture.BeginScope();
        var retirementDb = retirement.Resolve<CodeSpaceDbContext>();
        await using var retirementTransaction = await retirementDb.Database.BeginTransactionAsync();
        await retirementDb.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM workflow WHERE id = {seed.WorkflowId} FOR UPDATE");
        await retirementDb.WorkflowExternalTrigger.Where(trigger => trigger.Id == seed.TriggerId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(trigger => trigger.DeletedDate, DateTimeOffset.UtcNow)
                .SetProperty(trigger => trigger.KeyCiphertext, string.Empty));

        using var rotation = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var rotationDb = rotation.Resolve<CodeSpaceDbContext>();
        await rotationDb.Database.OpenConnectionAsync();
        var processId = await rotationDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var rotationTask = rotation.Resolve<IExternalTriggerStore>().RotateKeyAsync(
            seed.TriggerId, seed.TeamId, seed.UserId, CancellationToken.None);

        await WaitForLockAsync(processId);
        await retirementTransaction.CommitAsync();

        var error = await Record.ExceptionAsync(async () => await rotationTask);
        error.ShouldBeOfType<KeyNotFoundException>();
        using var verify = fixture.BeginScope();
        var retired = await verify.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.AsNoTracking()
            .SingleAsync(trigger => trigger.Id == seed.TriggerId);
        retired.DeletedDate.ShouldNotBeNull();
        retired.KeyCiphertext.ShouldBeEmpty();
    }

    private async Task WaitForLockAsync(int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var observer = fixture.BeginScope();
            var waitType = await observer.Resolve<CodeSpaceDbContext>().Database
                .SqlQueryRaw<string?>("SELECT wait_event_type AS \"Value\" FROM pg_stat_activity WHERE pid = {0}", processId)
                .SingleOrDefaultAsync();
            if (waitType == "Lock") return;
            await Task.Delay(10);
        }

        throw new TimeoutException("The key rotation did not wait for the workflow lock.");
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("rotate")]
    public async Task Another_teams_trigger_is_not_found(string operation)
    {
        var first = await SeedAsync();
        var other = await SeedAsync();
        using var scope = fixture.BeginScopeAs(other.UserId, other.TeamId);
        await Should.ThrowAsync<KeyNotFoundException>(() => ManageAsync(scope.Resolve<IMediator>(), operation, first.TriggerId));
        (await ReceiveAsync(first)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("rotate")]
    public async Task Deleting_the_workflow_retires_its_trigger_so_the_key_stops_resolving(string operation)
    {
        var seed = await SeedAsync();
        using (var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId))
            await scope.Resolve<IMediator>().Send(new DeleteWorkflowCommand { WorkflowId = seed.WorkflowId });

        using (var verify = fixture.BeginScopeAs(seed.UserId, seed.TeamId))
        {
            var error = await Record.ExceptionAsync(() => ManageAsync(verify.Resolve<IMediator>(), operation, seed.TriggerId));
            error.ShouldBeOfType<KeyNotFoundException>();
            FailureClassifier.Classify(error).Kind.ShouldBe(FailureKind.NotFound);
        }

        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
        using var audit = fixture.BeginScope();
        var db = audit.Resolve<CodeSpaceDbContext>();
        var row = await db.WorkflowExternalTrigger.AsNoTracking().SingleAsync(t => t.WorkflowId == seed.WorkflowId);
        row.DeletedDate.ShouldNotBeNull();
        row.KeyDigest.ShouldBe(ExternalTriggerKeySealer.Digest(seed.Key));
        row.KeyCiphertext.ShouldBeEmpty();
        (await db.WorkflowExternalTrigger.CountAsync(t => t.Id == seed.TriggerId && t.DeletedDate == null)).ShouldBe(0);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("告警")]
    [InlineData("")]
    public async Task A_legacy_name_is_not_a_workflow_creation_constraint(string desired)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        await CreateAsync(scope, Definition(desired));
        using var verify = fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().Workflow.CountAsync(w => w.TeamId == teamId)).ShouldBe(1);
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.CountAsync(w => w.TeamId == teamId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_unsupported_schema_keyword_is_refused_at_save_without_creating_a_workflow()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var config = JsonSerializer.SerializeToElement(new
        {
            slug = "unsupported-schema-" + Guid.NewGuid().ToString("N"),
            inputSchema = new { type = "object", properties = new { service = new { type = "string", pattern = "^a" } } },
        });
        var definition = Definition("unused") with
        {
            Nodes = Definition("unused").Nodes.Select(n => n.Id == "start" ? n with { Config = config } : n).ToArray(),
        };

        var error = await Record.ExceptionAsync(() => CreateAsync(scope, definition));

        error.ShouldNotBeNull();
        FailureClassifier.Classify(error).Kind.ShouldBe(FailureKind.Unprocessable);
        using var verify = fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().Workflow.CountAsync(w => w.TeamId == teamId)).ShouldBe(0);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{\"\\uD800\":{\"type\":\"string\"}}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"title\":\"\\uD800\"}}}")]
    [InlineData("{\"type\":\"object\",\"required\":[\"\\uD800\"]}")]
    public async Task An_unstorable_schema_is_refused_before_input_projection(string schema)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var config = WorkflowsTestSeed.Json("{\"inputSchema\":" + schema + "}");
        var definition = Definition("legacy") with
        {
            Nodes = Definition("legacy").Nodes.Select(n => n.Id == "start" ? n with { Config = config } : n).ToArray(),
        };
        var error = await Record.ExceptionAsync(() => CreateAsync(scope, definition));
        error.ShouldNotBeNull();
        FailureClassifier.Classify(error).Kind.ShouldBe(FailureKind.Unprocessable);
        using var verify = fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().Workflow.CountAsync(w => w.TeamId == teamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Two_teams_can_save_the_same_legacy_name_with_independent_trigger_identities()
    {
        var seed = await SeedAsync();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var workflowId = await CreateAsync(scope, Definition(seed.Slug));
        using var verify = fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.AsNoTracking()
            .SingleAsync(t => t.WorkflowId == workflowId);
        row.Id.ShouldNotBe((await RowAsync(seed)).Id);
        row.KeyDigest.ShouldNotBe(ExternalTriggerKeySealer.Digest(seed.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_or_replacing_the_node_retires_its_old_credential(bool replace)
    {
        var seed = await SeedAsync();
        var definition = Definition(seed.Slug);
        definition = definition with
        {
            Nodes = definition.Nodes.Select(n => n.Id != "start" ? n : replace
                ? n with { Id = "replacement" }
                : n with { TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson() }).ToArray(),
            Edges = [new EdgeDefinition { From = replace ? "replacement" : "start", To = "end" }],
        };
        await UpdateAsync(seed, definition);
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var rows = await scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.Where(t => t.WorkflowId == seed.WorkflowId).ToArrayAsync();
        var retired = rows.Single(t => t.NodeId == "start");
        retired.DeletedDate.ShouldNotBeNull();
        retired.KeyDigest.ShouldBe(ExternalTriggerKeySealer.Digest(seed.Key));
        retired.KeyCiphertext.ShouldBeEmpty();
        if (replace)
        {
            var replacement = rows.Single(t => t.NodeId == "replacement");
            replacement.Id.ShouldNotBe(seed.TriggerId);
            var fresh = await scope.Resolve<IMediator>().Send(new RevealExternalTriggerKeyCommand { TriggerId = replacement.Id });
            fresh.ShouldNotBe(seed.Key);
            (await ReceiveAsync(seed with { TriggerId = replacement.Id, Key = fresh })).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        }
    }

    private static JsonElement Config(string slug, string type = "string") => JsonSerializer.SerializeToElement(new
    {
        slug,
        inputSchema = new { type = "object", properties = new { service = new { type } }, required = new[] { "service" } },
    });

    private static WorkflowDefinition Definition(string slug) => WorkflowsTestSeed.MinimalDefinition() with
    {
        Nodes = [new NodeDefinition { Id = "start", TypeKey = "trigger.external", Config = Config(slug), Inputs = WorkflowsTestSeed.EmptyJson() },
            new NodeDefinition { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }],
    };

    private static WorkflowActivationInput[] Activations(WorkflowDefinition definition) => definition.Nodes
        .Where(n => n.TypeKey == "trigger.external")
        .Select(n => new WorkflowActivationInput { TypeKey = n.TypeKey, Config = n.Config }).ToArray();

    private static Task<Guid> CreateAsync(ILifetimeScope scope, WorkflowDefinition definition) => scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
    {
        Name = "External management", Definition = definition, Activations = Activations(definition),
    });

    private async Task<Seed> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        var slug = "alerts-" + Guid.NewGuid().ToString("N");
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var workflowId = await CreateAsync(scope, Definition(slug));
        var row = await scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.SingleAsync(t => t.WorkflowId == workflowId && t.DeletedDate == null);
        row.NodeId.ShouldBe("start");
        return new(teamId, userId, workflowId, row.Id, slug, scope.Resolve<ExternalTriggerKeySealer>().Open(row.KeyCiphertext));
    }

    private async Task UpdateAsync(Seed seed, WorkflowDefinition definition)
    {
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        await scope.Resolve<IMediator>().Send(new UpdateWorkflowCommand { WorkflowId = seed.WorkflowId, Name = "Edited", Definition = definition, Activations = Activations(definition) });
    }

    private async Task<WorkflowExternalTrigger> RowAsync(Seed seed)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.AsNoTracking().SingleAsync(t => t.WorkflowId == seed.WorkflowId && t.DeletedDate == null);
    }

    private async Task<ExternalTriggerOutcome> ReceiveAsync(Seed seed, string body = "{\"service\":\"api\"}")
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(seed.TriggerId, seed.Key, null, body, false), CancellationToken.None);
    }

    private static Task ManageAsync(IMediator mediator, string operation, Guid triggerId) => operation switch
    {
        "reveal" => mediator.Send(new RevealExternalTriggerKeyCommand { TriggerId = triggerId }),
        _ => mediator.Send(new RotateExternalTriggerKeyCommand { TriggerId = triggerId }),
    };
}
