using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 The <c>plan.author</c> node end-to-end through the REAL engine over real Postgres (triad S1): the node
/// runs the production <c>LlmWorkflowPlanner</c> (real registry + pool resolution by the pinned row id, real
/// structured call, real <c>PlannerSchema.Options</c> deserialization), persists a durable <c>work_plan</c>
/// version through the real store, and surfaces planId/items/executionNeeded on its outputs. Faked at the
/// honest <c>IStructuredLLMClient</c> seam only (<see cref="DeterministicWorkPlanLlmClient"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PlanAuthorNodeFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public PlanAuthorNodeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<WorkPlanPlanScript>().Reset();   // the knob is fixture-shared — never leak into sibling tests
    }

    [Fact]
    public async Task Plan_author_persists_the_contract_carrying_plan_and_binds_its_outputs()
    {
        using (var s = _fixture.BeginScope()) s.Resolve<WorkPlanPlanScript>().AuthorContract = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var workflowId = await CreatePlanWorkflowAsync(teamId, userId, plannerRowId, singleNode: true);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the plan node authored + persisted and the run completed; if not, inspect the failed WorkflowRunNode rows for this runId");

        // ── The durable artifact: one work_plan version, produced by the node, carrying the FULL contract. ──
        var plan = await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId);
        plan.TeamId.ShouldBe(teamId);
        plan.Version.ShouldBe(1);
        plan.OriginKind.ShouldBe(WorkPlanOrigins.Node);
        plan.OriginKey.ShouldBeNull("the node path is version-per-execution — no exactly-once key");
        plan.Goal.ShouldBe(DeterministicWorkPlanLlmClient.PlannedGoal);
        plan.Status.ShouldBe(WorkPlanStatuses.Authored);

        var items = JsonDocument.Parse(plan.ItemsJson).RootElement;
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("id").GetString().ShouldBe("s1");
        items[1].TryGetProperty("dependsOn", out var deps).ShouldBeTrue(customMessage: $"the persisted items_json was: {plan.ItemsJson}");
        deps[0].GetString().ShouldBe("s1", "the model-authored DAG edge survives into the persisted item");
        var acceptance = items[1].GetProperty("acceptance");
        acceptance.GetProperty("command").EnumerateArray().Select(e => e.GetString()).ShouldBe(DeterministicWorkPlanLlmClient.AcceptanceCommand,
            "the per-item objective acceptance — the sprint-contract half of the plan — survives into the persisted item");
        acceptance.GetProperty("kind").GetString().ShouldBe("TestsPass", "the enum serializes as its NAME (AgentJson string enums), matching the supervisor tape vocabulary");
        items[0].TryGetProperty("acceptance", out _).ShouldBeFalse("an uncontracted item omits acceptance entirely — null-omitted, not null-valued");
        items[0].GetProperty("kind").GetString().ShouldBe("research", "the open item kind survives into the persisted item");
        items[1].GetProperty("acceptanceCriteria")[0].GetString().ShouldBe("covers edge cases", "the SUBJECTIVE per-item criteria survive alongside the objective spec");

        // The plan-level contract enrichment: recorded assumptions + the operator-question form fodder.
        JsonDocument.Parse(plan.AssumptionsJson!).RootElement[0].GetString().ShouldBe("assumed the default branch");
        var questions = JsonDocument.Parse(plan.QuestionsJson!).RootElement;
        questions.GetArrayLength().ShouldBe(1);
        questions[0].GetProperty("id").GetString().ShouldBe("q1");
        questions[0].GetProperty("options").GetArrayLength().ShouldBe(2);
        questions[0].GetProperty("recommendedOptionId").GetString().ShouldBe("a");

        // The checklist read model: contract verbatim + honestly-Pending execution state (no linkage on the graph tier yet).
        var checklist = await verify.Resolve<IWorkPlanChecklistService>().GetCurrentAsync(runId, teamId, CancellationToken.None);
        checklist.ShouldNotBeNull();
        checklist!.Questions!.Count.ShouldBe(1);
        checklist.Assumptions!.Count.ShouldBe(1);
        checklist.Items.Count.ShouldBe(2);
        checklist.Items.ShouldAllBe(i => i.State == WorkPlanItemStates.Pending && i.Attempts == 0);
        checklist.Items[1].Item.AcceptanceCriteria!.Count.ShouldBe(1);

        // ── The node outputs: planId/version bind the artifact; items mirrors the persisted bytes; executionNeeded=true. ──
        var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "plan" && n.IterationKey == "");
        var outputs = JsonDocument.Parse(node.OutputsJson!).RootElement;
        outputs.GetProperty("planId").GetString().ShouldBe(plan.Id.ToString());
        outputs.GetProperty("version").GetInt32().ShouldBe(1);
        outputs.GetProperty("executionNeeded").GetBoolean().ShouldBeTrue();
        outputs.GetProperty("items").GetArrayLength().ShouldBe(2);
        outputs.GetProperty("json").GetProperty("subtasks").GetArrayLength().ShouldBe(2, "the raw plan rides 'json' — binding-compatible with a structured llm.complete");
    }

    [Fact]
    public async Task A_second_plan_author_execution_appends_the_runs_next_version()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var workflowId = await CreatePlanWorkflowAsync(teamId, userId, plannerRowId, singleNode: false);   // plan → plan2 → end
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var plans = await db.WorkPlan.AsNoTracking().Where(p => p.WorkflowRunId == runId).OrderBy(p => p.Version).ToListAsync();
        plans.Count.ShouldBe(2, "each plan.author execution persists the run's NEXT version — the edit-loop contract");
        plans.Select(p => p.Version).ShouldBe(new[] { 1, 2 });
        plans[0].Id.ShouldNotBe(plans[1].Id);

        var second = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "plan2" && n.IterationKey == "");
        JsonDocument.Parse(second.OutputsJson!).RootElement.GetProperty("version").GetInt32().ShouldBe(2, "the second node's outputs carry ITS version");
    }

    [Fact]
    public async Task A_flat_plan_strips_authored_dependencies_but_keeps_the_rest_of_the_contract()
    {
        // The rich contract authors dependsOn on s2 — under flatPlan (a parallel fan-out consumer) the persisted
        // contract must NOT carry ordering the map cannot honor; the acceptance + criteria survive untouched.
        using (var s = _fixture.BeginScope()) s.Resolve<WorkPlanPlanScript>().AuthorContract = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var workflowId = await CreateFlatPlanWorkflowAsync(teamId, userId, plannerRowId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var plan = await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId);
        var items = JsonDocument.Parse(plan.ItemsJson).RootElement.EnumerateArray().ToList();

        items.Count.ShouldBe(2);
        items.Any(i => i.TryGetProperty("dependsOn", out var deps) && deps.ValueKind != JsonValueKind.Null)
            .ShouldBeFalse("flatPlan strips ordering the parallel map cannot honor — the checklist must never show dependency chips the execution ignored");
        items.Single(i => i.GetProperty("id").GetString() == "s2").TryGetProperty("acceptance", out _)
            .ShouldBeTrue("only the ORDERING is stripped — the per-item acceptance contract survives");
    }

    private async Task<Guid> CreateFlatPlanWorkflowAsync(Guid teamId, Guid userId, Guid plannerRowId)
    {
        var config = WorkflowsTestSeed.Json($$"""{"plannerModelId":"{{plannerRowId}}","flatPlan":true}""");

        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "plan", TypeKey = "plan.author", Config = config, Inputs = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        };
        var edges = new List<EdgeDefinition> { new() { From = "start", To = "plan" }, new() { From = "plan", To = "end" } };

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "plan-flat-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    [Fact]
    public async Task A_structurally_invalid_dag_fails_the_node_closed()
    {
        using (var s = _fixture.BeginScope()) s.Resolve<WorkPlanPlanScript>().AuthorInvalidDag = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var workflowId = await CreatePlanWorkflowAsync(teamId, userId, plannerRowId, singleNode: true);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Failure, "a dangling dependsOn must fail the plan node CLOSED — a broken graph never becomes the contract");

        var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "plan" && n.IterationKey == "");
        node.Error.ShouldNotBeNull();
        node.Error!.ShouldContain("structurally invalid", customMessage: "the failure names the structural contradiction legibly");

        (await db.WorkPlan.AsNoTracking().CountAsync(p => p.WorkflowRunId == runId)).ShouldBe(0, "nothing was persisted for the invalid plan");
    }

    [Fact]
    public async Task Has_enough_context_surfaces_execution_not_needed()
    {
        using (var s = _fixture.BeginScope()) s.Resolve<WorkPlanPlanScript>().HasEnoughContext = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var workflowId = await CreatePlanWorkflowAsync(teamId, userId, plannerRowId, singleNode: true);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "plan" && n.IterationKey == "");
        JsonDocument.Parse(node.OutputsJson!).RootElement.GetProperty("executionNeeded").GetBoolean()
            .ShouldBeFalse("the planner's self-bypass (hasEnoughContext) surfaces as executionNeeded=false for a downstream logic.if");

        (await db.WorkPlan.AsNoTracking().CountAsync(p => p.WorkflowRunId == runId)).ShouldBe(1, "the plan is persisted either way — the bypass routes execution, it doesn't skip the artifact");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>manual → plan (plan.author, planner pinned to the fake's pool row) [→ plan2] → terminal.</summary>
    private async Task<Guid> CreatePlanWorkflowAsync(Guid teamId, Guid userId, Guid plannerRowId, bool singleNode)
    {
        var config = WorkflowsTestSeed.Json($$"""{"plannerModelId":"{{plannerRowId}}"}""");

        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "plan", TypeKey = "plan.author", Config = config, Inputs = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}""") },
        };
        var edges = new List<EdgeDefinition> { new() { From = "start", To = "plan" } };

        if (!singleNode)
        {
            nodes.Add(new() { Id = "plan2", TypeKey = "plan.author", Config = config, Inputs = WorkflowsTestSeed.Json("""{"goal":"ship the feature","feedback":"tighten the second step"}""") });
            edges.Add(new() { From = "plan", To = "plan2" });
        }

        nodes.Add(new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() });
        edges.Add(new() { From = singleNode ? "plan" : "plan2", To = "end" });

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "plan-author-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }
}
