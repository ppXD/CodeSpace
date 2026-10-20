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
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExternalTriggerManagementFlowTests(PostgresFixture fixture)
{
    private sealed record Seed(Guid TeamId, Guid UserId, Guid WorkflowId, string Slug, string Key);

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
        after.Slug.ShouldBe(seed.Slug);
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
    public async Task An_unusable_draft_is_refused_without_damaging_the_live_endpoint()
    {
        var seed = await SeedAsync();
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        var before = await RowAsync(seed);

        var error = await Should.ThrowAsync<ArgumentException>(() => UpdateAsync(seed, Definition("告警")));

        var after = await RowAsync(seed);
        after.Id.ShouldBe(before.Id);
        after.Slug.ShouldBe(before.Slug);
        after.KeyDigest.ShouldBe(before.KeyDigest);
        after.KeyCiphertext.ShouldBe(before.KeyCiphertext);
        var outcome = await ReceiveAsync(seed);
        outcome.Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        outcome.RunId.ShouldNotBe(Guid.Empty);
        using var scope = fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.SingleAsync(r => r.Id == outcome.RunId);
        run.WorkflowId.ShouldBe(seed.WorkflowId);

        error.ParamName.ShouldBe("slug");
        error.Message.ShouldContain("slug");
        error.Message.ShouldContain("Hyphens and underscores are allowed");
    }

    [Fact]
    public async Task Reveal_hands_back_the_minted_key()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        (await scope.Resolve<IMediator>().Send(new RevealExternalTriggerKeyCommand { Slug = seed.Slug })).ShouldBe(seed.Key);
    }

    [Theory]
    [InlineData(TeamRole.Viewer, "reveal")]
    [InlineData(TeamRole.Viewer, "rotate")]
    [InlineData(TeamRole.Viewer, "rename")]
    [InlineData(TeamRole.Member, "reveal")]
    [InlineData(TeamRole.Member, "rotate")]
    [InlineData(TeamRole.Member, "rename")]
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
        var error = await Record.ExceptionAsync(() => ManageAsync(scope.Resolve<IMediator>(), operation, seed.Slug, "renamed-" + Guid.NewGuid().ToString("N")));
        if (role == TeamRole.Viewer) error.ShouldBeOfType<TenantAccessDeniedException>();
        else error.ShouldBeNull();
    }

    [Fact]
    public async Task Rotating_invalidates_the_old_key_immediately_and_reveal_returns_the_new_key()
    {
        var seed = await SeedAsync();
        string fresh;
        using (var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId))
            fresh = await scope.Resolve<IMediator>().Send(new RotateExternalTriggerKeyCommand { Slug = seed.Slug });
        fresh.ShouldNotBe(seed.Key);
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Unauthorized);
        (await ReceiveAsync(seed with { Key = fresh })).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        await UpdateAsync(seed, Definition(seed.Slug));
        using var verify = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        (await verify.Resolve<IMediator>().Send(new RevealExternalTriggerKeyCommand { Slug = seed.Slug })).ShouldBe(fresh);
    }

    [Fact]
    public async Task Renaming_moves_the_endpoint_and_a_stale_draft_save_does_not_move_it_back()
    {
        var seed = await SeedAsync();
        var renamed = "prod-alerts-v2-" + Guid.NewGuid().ToString("N");
        using (var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId))
            await scope.Resolve<IMediator>().Send(new RenameExternalTriggerCommand { Slug = seed.Slug, DesiredSlug = renamed });
        await UpdateAsync(seed, Definition(seed.Slug));
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.NotFound);
        (await ReceiveAsync(seed with { Slug = renamed })).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        (await RowAsync(seed)).KeyDigest.ShouldBe(ExternalTriggerKeySealer.Digest(seed.Key));
    }

    [Fact]
    public async Task Renaming_onto_another_teams_live_slug_is_refused_and_original_is_untouched()
    {
        var first = await SeedAsync();
        var other = await SeedAsync();
        using var scope = fixture.BeginScopeAs(other.UserId, other.TeamId);
        var error = await Record.ExceptionAsync(() => scope.Resolve<IMediator>().Send(new RenameExternalTriggerCommand { Slug = other.Slug, DesiredSlug = first.Slug }));
        error.ShouldNotBeNull();
        FailureClassifier.Classify(error).Kind.ShouldBe(FailureKind.Conflict);
        (await ReceiveAsync(other)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
        (await ReceiveAsync(first)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("rotate")]
    [InlineData("rename")]
    public async Task Another_teams_slug_is_not_found(string operation)
    {
        var first = await SeedAsync();
        var other = await SeedAsync();
        using var scope = fixture.BeginScopeAs(other.UserId, other.TeamId);
        await Should.ThrowAsync<KeyNotFoundException>(() => ManageAsync(scope.Resolve<IMediator>(), operation, first.Slug, "unused"));
        (await ReceiveAsync(first)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("告警")]
    [InlineData("")]
    public async Task An_underivable_slug_is_refused_without_creating_a_workflow(string desired)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var error = await Record.ExceptionAsync(() => CreateAsync(scope, Definition(desired)));
        error.ShouldNotBeNull();
        FailureClassifier.Classify(error).Kind.ShouldBeOneOf(FailureKind.Invalid, FailureKind.Unprocessable);
        using var verify = fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().Workflow.CountAsync(w => w.TeamId == teamId)).ShouldBe(0);
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.CountAsync(w => w.TeamId == teamId)).ShouldBe(0);
    }

    [Fact]
    public async Task An_underivable_rename_is_refused_and_keeps_the_old_endpoint()
    {
        var seed = await SeedAsync();
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        await Should.ThrowAsync<ArgumentException>(() => scope.Resolve<IMediator>().Send(new RenameExternalTriggerCommand { Slug = seed.Slug, DesiredSlug = "!!!" }));
        (await ReceiveAsync(seed)).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
    }

    [Fact]
    public async Task Creating_with_an_existing_slug_is_a_conflict_not_a_suffixed_endpoint()
    {
        var seed = await SeedAsync();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var error = await Record.ExceptionAsync(() => CreateAsync(scope, Definition(seed.Slug)));
        error.ShouldNotBeNull();
        FailureClassifier.Classify(error).Kind.ShouldBe(FailureKind.Conflict);
        using var verify = fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().Workflow.CountAsync(w => w.TeamId == teamId)).ShouldBe(0);
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
        (await ReceiveAsync(seed)).Kind.ShouldBe(replace ? ExternalTriggerOutcomeKind.Unauthorized : ExternalTriggerOutcomeKind.NotFound);
        using var scope = fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var rows = await scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.Where(t => t.WorkflowId == seed.WorkflowId).ToArrayAsync();
        rows.Single(t => t.NodeId == "start").DeletedDate.ShouldNotBeNull();
        if (replace)
        {
            var fresh = await scope.Resolve<IMediator>().Send(new RevealExternalTriggerKeyCommand { Slug = seed.Slug });
            fresh.ShouldNotBe(seed.Key);
            (await ReceiveAsync(seed with { Key = fresh })).Kind.ShouldBe(ExternalTriggerOutcomeKind.Accepted);
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
        row.Slug.ShouldBe(slug);
        return new(teamId, userId, workflowId, slug, scope.Resolve<ExternalTriggerKeySealer>().Open(row.KeyCiphertext));
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
        return await scope.Resolve<IExternalTriggerIngress>().ReceiveAsync(new(seed.Slug, seed.Key, null, body, false), CancellationToken.None);
    }

    private static Task ManageAsync(IMediator mediator, string operation, string slug, string desired) => operation switch
    {
        "reveal" => mediator.Send(new RevealExternalTriggerKeyCommand { Slug = slug }),
        "rotate" => mediator.Send(new RotateExternalTriggerKeyCommand { Slug = slug }),
        _ => mediator.Send(new RenameExternalTriggerCommand { Slug = slug, DesiredSlug = desired }),
    };
}
