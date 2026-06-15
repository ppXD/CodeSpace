using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// PR-D Slice 1 integration: the task-first planner end-to-end. The <c>PlanWorkflowFromTaskCommand</c>
/// (team from <c>ICurrentTeam</c>, never the body) drives the real <c>WorkflowPlanningService</c> →
/// <c>LlmWorkflowPlanner</c> (routed through the structured-LLM fake at the real IStructuredLLMClient seam)
/// → <c>WorkflowPlanProjector</c>. We assert the projected definition VALIDATES, then persist it as a
/// workflow and RUN IT THROUGH THE ENGINE: it suspends on the plan-review approval wait, we approve it, and
/// the projected <c>flow.map</c> fans out over the planned subtasks (one branch per subtask) and the run
/// completes.
///
/// <para>Fidelity (Rule 12): the engine (CAS suspend/resume, the map wait-for-all barrier, RehydrateMapResults),
/// real Postgres, the full command → handler → service → planner → projector path, and the real
/// DefinitionValidator are ALL real. Faked at an honest boundary: the LLM (the
/// <see cref="DeterministicTaskPlannerLlmClient"/> at the IStructuredLLMClient / ILLMClient seam). The planning
/// call resolves it via a child-scope registry holding only that client (deterministic plan); the projected
/// llm.complete nodes (singletons bound to the ROOT registry) reach it after we retarget their provider to the
/// fake tag — so the flow runs with no API key.</para>
///
/// <para>Scope: this exercises the ANALYSIS path (recommendedWorkflowKind="analysis" → an llm.complete body),
/// which runs with no sandbox. That BOTH projection shapes (analysis→llm.complete and coding→agent.code) emit a
/// definition the real DefinitionValidator accepts is pinned by the unit Theory
/// (WorkflowPlannerTests.Projection_of_a_representative_plan_passes_DefinitionValidator); the agent.code body's
/// real RUNNABILITY is covered by the planner→map→real-agent E2E (PR-D2). Together they cover both paths without
/// requiring a sandbox here.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PlannerProjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public PlannerProjectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Plan_projects_a_valid_definition_that_runs_suspends_on_approval_then_fans_out_over_the_subtasks()
    {
        Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, "1");
        try
        {
            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

            // ── Plan from a task via the command — team resolved from ICurrentTeam, never the body. ──
            var result = await PlanFromTaskAsync(teamId, userId, "Improve the onboarding module");

            result.PlannerEnabled.ShouldBeTrue();
            result.Plan.ShouldNotBeNull();
            result.Definition.ShouldNotBeNull();
            result.Plan!.Subtasks.Count.ShouldBe(DeterministicTaskPlannerLlmClient.SubtaskTitles.Count);

            // The projection is valid (assert independently of the service's own pre-return validation).
            Validate(result.Definition!).IsValid.ShouldBeTrue();

            // ── Persist + run. Retarget the llm.complete nodes to the fake provider so the engine runs with no key. ──
            var runnable = RetargetLlmToFake(result.Definition!);
            var workflowId = await CreateWorkflowAsync(teamId, userId, runnable);
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            // ── Pass 1: the run suspends on the plan-review approval wait. ──
            await RunEngineAsync(runId);
            await AssertSuspendedOnApprovalAsync(runId);

            // ── Approve → re-run: the map fans out one branch per subtask and the run completes. ──
            (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
            await RunEngineAsync(runId);

            await AssertCompletedAndFannedOutAsync(runId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, null);
        }
    }

    // ─── Assertions ──────────────────────────────────────────────────────────

    private async Task AssertSuspendedOnApprovalAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);

        var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
        wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval, "the projected graph pauses for human plan review before fanning out");
    }

    private async Task AssertCompletedAndFannedOutAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "after approval the map should fan out over the planned subtasks and the run reach Success; if not, inspect the failed WorkflowRunNode rows for this runId");

        // The map reduce's own bookkeeping proves the fan-out width = the number of planned subtasks — i.e. the
        // {{input.subtasks}} binding resolved the baked default the projector emitted.
        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var mapOut = JsonDocument.Parse(mapNode.OutputsJson!).RootElement;
        mapOut.GetProperty("count").GetInt32().ShouldBe(DeterministicTaskPlannerLlmClient.SubtaskTitles.Count,
            "one branch fanned out per planned subtask");
        mapOut.GetProperty("failed").GetInt32().ShouldBe(0);

        // Prove the LOAD-BEARING resolution, not just the width: the projector bakes subtasks camelCase so the body's
        // case-sensitive {{item.title}}/{{item.instruction}} refs resolve. Branch map#0's body echoed the FIRST
        // subtask's title+instruction (the fake returns "done: {prompt}") — if camelCase resolution silently failed,
        // the prompt would be literal "{{item.title}}: ..." and this would miss the real title.
        var firstTitle = DeterministicTaskPlannerLlmClient.SubtaskTitles[0];
        var bodyBranch = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "body" && n.IterationKey == "map#0");
        var bodyText = JsonDocument.Parse(bodyBranch.OutputsJson!).RootElement.GetProperty("text").GetString();
        bodyText.ShouldContain(firstTitle, Case.Sensitive, $"branch map#0 must resolve {{{{item.title}}}} to the planned subtask '{firstTitle}' — proves the camelCase-baked subtasks resolve through the case-sensitive item refs");
        bodyText.ShouldNotContain("{{item.", customMessage: "an unresolved {{item.*}} ref means the baked-default key casing didn't match the body refs");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<PlanWorkflowFromTaskResult> PlanFromTaskAsync(Guid teamId, Guid userId, string taskText)
    {
        // Child-scope registry holds ONLY the planner fake → LlmWorkflowPlanner resolves it deterministically
        // (the planner + service are scoped, so they pick up this override).
        using var scope = _fixture.BeginScope(b =>
        {
            RegisterCaller(b, userId, teamId);
            b.RegisterInstance(new LLMClientRegistry(new ILLMClient[] { new DeterministicTaskPlannerLlmClient() })).As<ILLMClientRegistry>().SingleInstance();
        });

        return await scope.Resolve<IMediator>().Send(new PlanWorkflowFromTaskCommand { TaskText = taskText });
    }

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope(b => RegisterCaller(b, userId, teamId));
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = true, Comment = "go" });
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScope(b => RegisterCaller(b, userId, teamId));
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "planned-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private ValidationResult Validate(WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<DefinitionValidator>().Validate(definition);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static void RegisterCaller(ContainerBuilder b, Guid userId, Guid teamId)
    {
        b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
        b.RegisterInstance(new TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
    }

    /// <summary>
    /// Test-only adaptation: rewrite every llm.complete node's provider from the production default
    /// (<c>Anthropic</c>) to the registered fake's tag, so the engine resolves the fake (no API key). The graph
    /// SHAPE — and the projector that built it — is untouched; only the provider string differs.
    /// </summary>
    private static WorkflowDefinition RetargetLlmToFake(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(RetargetNode).ToList(),
    };

    private static NodeDefinition RetargetNode(NodeDefinition node)
    {
        if (node.TypeKey != "llm.complete") return node;

        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(DeterministicTaskPlannerLlmClient.ProviderTag);

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }
}
