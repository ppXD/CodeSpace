using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end persistence integrity for the workflow definition pipeline. Three concerns
/// share this file because they all hit the JSON ↔ DTO ↔ DB triangle:
///
///   1. <b>Round-trip preservation</b> — what the editor POSTs (Variables, Inputs, Outputs,
///      unknown keys in node.Config) is byte-equivalent to what a subsequent GET returns.
///      Catches accidental DTO trimming, casing drift, enum/string coercion bugs.
///   2. <b>Validation error shape</b> — invalid definitions throw
///      <see cref="WorkflowValidationException"/> with structured <c>Errors</c> the editor
///      can render one banner per issue.
///   3. <b>Defensive deserialization</b> — empty/null/garbage JSON columns degrade to an
///      empty object instead of 500'ing the read endpoint. Migrations sometimes leave
///      legacy rows in odd shapes; the read path must survive that.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WorkflowRoundTripFlowTests
{
    private readonly PostgresFixture _fixture;
    public WorkflowRoundTripFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Round-trip preservation ────────────────────────────────────────────────

    // Environment-variable round-trip coverage lives in `VariableServiceFlowTests` (CRUD per
    // scope + tenant isolation) because env vars live in the unified `variable` table with
    // scope=Team, not in the workflow definition JSON.

    [Fact]
    public async Task Inputs_outputs_round_trip_with_full_schemas()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Inputs and Outputs are part of the workflow's IO contract (declared at design
        // time, consumed at run time), so their round-trip preservation belongs here.
        var def = WorkflowsTestSeed.MinimalDefinition() with
        {
            Inputs = new[]
            {
                new WorkflowVariable
                {
                    Name = "tone",
                    Label = "Comment tone",
                    Schema = WorkflowsTestSeed.Json("""{"type":"string","enum":["friendly","strict"]}"""),
                    Required = true,
                },
            },
            Outputs = new[]
            {
                new WorkflowVariable
                {
                    Name = "summary",
                    Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
                },
            },
        };

        var workflowId = await CreateAsync(teamId, userId, def);
        var fetched = await GetAsync(teamId, userId, workflowId);

        fetched.ShouldNotBeNull();
        fetched.Definition.Inputs.ShouldNotBeNull();
        fetched.Definition.Inputs!.Count.ShouldBe(1);
        fetched.Definition.Inputs[0].Required.ShouldBeTrue();
        fetched.Definition.Inputs[0].Schema.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "friendly", "strict" });

        fetched.Definition.Outputs.ShouldNotBeNull();
        fetched.Definition.Outputs!.Count.ShouldBe(1);
        fetched.Definition.Outputs[0].Name.ShouldBe("summary");
    }

    [Fact]
    public async Task Unknown_keys_inside_node_config_are_preserved_round_trip()
    {
        // The engine treats node.Config and node.Inputs as opaque JSON — any property the
        // node's manifest declares (or any future-proofing extra) MUST round-trip untouched.
        // Without this guarantee, an old client + new manifest combination could silently
        // strip operator-set values on the first save.

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var configWithExtras = WorkflowsTestSeed.Json("""
            {
              "repositoryId": "11111111-1111-1111-1111-111111111111",
              "labels": ["security", "infra"],
              "experimental": { "futurePolicy": "fast-fail", "version": 2 }
            }
            """);

        var baseDef = WorkflowsTestSeed.MinimalDefinition();
        var def = baseDef with
        {
            Nodes = new List<NodeDefinition>
            {
                baseDef.Nodes[0] with { Config = configWithExtras },
                baseDef.Nodes[1],
            },
        };

        var workflowId = await CreateAsync(teamId, userId, def);
        var fetched = await GetAsync(teamId, userId, workflowId);
        var triggerNodeConfig = fetched!.Definition.Nodes.Single(n => n.Id == "start").Config;

        triggerNodeConfig.GetProperty("repositoryId").GetString().ShouldBe("11111111-1111-1111-1111-111111111111");
        triggerNodeConfig.GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "security", "infra" });
        triggerNodeConfig.GetProperty("experimental").GetProperty("futurePolicy").GetString().ShouldBe("fast-fail");
        triggerNodeConfig.GetProperty("experimental").GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Update_then_get_returns_definition_byte_equivalent_to_what_was_posted()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        var updated = WorkflowsTestSeed.MinimalDefinition() with
        {
            Inputs = new[]
            {
                new WorkflowVariable { Name = "policy", Schema = WorkflowsTestSeed.Json("""{"type":"string"}""") },
            },
        };

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new UpdateWorkflowCommand
            {
                WorkflowId = workflowId,
                Name = "Renamed",
                Description = "After update",
                Definition = updated,
                Activations = new List<WorkflowActivationInput>(),
            });
        }

        var fetched = await GetAsync(teamId, userId, workflowId);
        fetched.ShouldNotBeNull();

        // Re-serialise both shapes with the same options. The Definition we POSTed had only
        // Variables added; the fetched one must have the same shape (Variables present,
        // Inputs/Outputs unchanged, Nodes/Edges identical to the minimal definition).
        var fetchedJson = JsonSerializer.Serialize(fetched.Definition, WorkflowJson.Options);
        var expectedJson = JsonSerializer.Serialize(updated, WorkflowJson.Options);
        fetchedJson.ShouldBe(expectedJson, "GET must return exactly what the editor PUT");
    }

    // ─── Validation error shape ─────────────────────────────────────────────────

    [Fact]
    public async Task Invalid_definition_throws_WorkflowValidationException_with_per_issue_errors()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Triple violation: two triggers (>1 not allowed), no terminal, dangling edge.
        var bad = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "t1", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "t2", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t1", To = "ghost" }, // ghost doesn't exist
            },
        };

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<WorkflowValidationException>(async () =>
        {
            await mediator.Send(new CreateWorkflowCommand
            {
                Name = "invalid",
                Description = null,
                Definition = bad,
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        });

        // Each distinct problem MUST appear as its own entry — the editor renders one row per
        // error. A single joined message would force operators to fix them one at a time.
        ex.Errors.Count.ShouldBeGreaterThanOrEqualTo(3,
            "expected at least 3 errors: trigger count, missing terminal, dangling edge");
        ex.Errors.ShouldContain(e => e.Contains("Trigger", StringComparison.OrdinalIgnoreCase));
        ex.Errors.ShouldContain(e => e.Contains("Terminal", StringComparison.OrdinalIgnoreCase));
        ex.Errors.ShouldContain(e => e.Contains("ghost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_scope_head_in_node_inputs_is_flagged_with_actionable_message()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // `bogus.x` is not a recognised scope head — validator must call out the allowed list.
        var baseDef = WorkflowsTestSeed.MinimalDefinition();
        var def = baseDef with
        {
            Nodes = new List<NodeDefinition>
            {
                baseDef.Nodes[0],
                baseDef.Nodes[1] with { Inputs = WorkflowsTestSeed.Json("""{"summary":"{{bogus.x}}"}""") },
            },
        };

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<WorkflowValidationException>(async () =>
        {
            await mediator.Send(new CreateWorkflowCommand
            {
                Name = "bad-ref",
                Description = null,
                Definition = def,
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        });

        ex.Errors.ShouldContain(e => e.Contains("bogus") && e.Contains("scope head"),
            "validator must name the unknown head AND list the legal alternatives");
    }

    [Fact]
    public async Task Reference_to_non_upstream_node_is_flagged_with_actionable_message()
    {
        // Three nodes: trigger → a, trigger → b. Node b references a's output, but they're
        // parallel branches — a isn't upstream of b. Operator must add an edge or restructure.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "trig", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "a",    TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "b",    TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(),
                        Inputs = WorkflowsTestSeed.Json("""{"echo":"{{nodes.a.outputs.title}}"}""") },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "trig", To = "a" },
                new() { From = "trig", To = "b" },
            },
        };

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<WorkflowValidationException>(async () =>
        {
            await mediator.Send(new CreateWorkflowCommand
            {
                Name = "parallel-bad-ref",
                Description = null,
                Definition = def,
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        });

        ex.Errors.ShouldContain(e => e.Contains("'a'") && e.Contains("not upstream"),
            "validator must name the offending node id and the 'not upstream' relationship");
    }

    // ─── Defensive deserialization ──────────────────────────────────────────────

    [Fact]
    public async Task GetRun_returns_empty_object_for_normalized_payload_and_outputs_on_run_without_terminal()
    {
        // The payload lives on workflow_run_request.normalized_payload_json and is always
        // present (NOT NULL DEFAULT '{}'::jsonb). The defensive scenario is a run that
        // hasn't populated OutputsJson yet (defaults to '{}'::jsonb at the schema level).
        // The GET endpoint must surface both as empty JSON objects ready for serialization
        // back to the SPA — never null, never throw.

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // workflow_run_node is a view over workflow_run_record. To force a pre-completed
        // cell to surface in the view, emit a node.started record via the logger. The view
        // projects this as Running with the empty default inputs/outputs.
        using (var write = _fixture.BeginScope())
        {
            var logger = write.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>();
            await logger.NodeStartedAsync(runId, "start", iterationKey: "",
                resolvedInputs: new Dictionary<string, JsonElement>(),
                resolvedConfig: new Dictionary<string, JsonElement>(),
                cancellationToken: CancellationToken.None);
        }

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var detail = await mediator.Send(new GetWorkflowRunQuery { RunId = runId });

        detail.ShouldNotBeNull();
        // Every JSON field projects to an empty object — the SPA's run-detail panel renders
        // safely against this without per-field null checks.
        detail.NormalizedPayload.ValueKind.ShouldBe(JsonValueKind.Object);
        detail.NormalizedPayload.EnumerateObject().ShouldBeEmpty();
        detail.Outputs.ValueKind.ShouldBe(JsonValueKind.Object);
        detail.Outputs.EnumerateObject().ShouldBeEmpty();
        detail.Nodes.Count.ShouldBe(1);
        detail.Nodes[0].Inputs.ValueKind.ShouldBe(JsonValueKind.Object);
        detail.Nodes[0].Outputs.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task GetWorkflow_surfaces_activation_with_empty_config_as_object_not_throw()
    {
        // jsonb NOT NULL DEFAULT '{}'::jsonb means an activation row inserted via raw SQL
        // (or a default-only constructor) still arrives back as a parseable empty object.
        // The GET endpoint must project that to JsonElement Object, not crash on whatever
        // EF/Npgsql gives back from a default-only column.

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        using (var write = _fixture.BeginScope())
        {
            var db = write.Resolve<CodeSpaceDbContext>();
            db.WorkflowActivation.Add(new WorkflowActivation
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflowId,
                TypeKey = "trigger.pr.opened",
                // Leave ConfigJson at its EF default of "{}" — same shape as a freshly-added
                // activation that hasn't been edited yet.
                Enabled = true,
                CreatedBy = SystemUsers.SeederId,
                LastModifiedBy = SystemUsers.SeederId,
            });
            await db.SaveChangesAsync();
        }

        var fetched = await GetAsync(teamId, userId, workflowId);
        fetched.ShouldNotBeNull();
        var emptyActivation = fetched.Activations.Single(a => a.Config.ValueKind == JsonValueKind.Object && !a.Config.EnumerateObject().Any());
        emptyActivation.TypeKey.ShouldBe("trigger.pr.opened");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "round-trip-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<WorkflowDetail?> GetAsync(Guid teamId, Guid userId, Guid workflowId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new GetWorkflowQuery { WorkflowId = workflowId });
    }
}
