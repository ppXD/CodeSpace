using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// PR-D.5 CROWN-JEWEL integration: the L3 CHECKPOINT-COORDINATED plan RUNS THROUGH THE REAL ENGINE across
/// MULTIPLE ROUNDS. A coordinated <c>PlanWorkflowFromTaskCommand</c> drives the real
/// <c>WorkflowPlanningService</c> → <c>LlmWorkflowPlanner</c> → <c>WorkflowPlanProjector.ProjectCoordinated</c>,
/// producing a <c>flow.loop</c> graph the real <c>DefinitionValidator</c> accepts. We persist it, run it, and
/// prove the loop re-decides between rounds:
///
/// <list type="bullet">
/// <item>The run suspends on the plan-review approval; we approve.</item>
/// <item>Round 1: the map fans over the two PLANNED subtasks; the coordinator decides <c>rework</c> (with a
///   THREE-subtask rework set), so the loop re-runs.</item>
/// <item>Round 2: the map fans over the THREE REWORK subtasks; the coordinator decides <c>done</c>, so the
///   loop TERMINATES on the done condition.</item>
/// <item>The synthesizer runs, and the run reaches Success. The loop executed EXACTLY 2 iterations (not the
///   round cap of 5).</item>
/// </list>
///
/// <para>Fidelity (Rule 12, high): the engine (loop re-walk + state rehydration, the map wait-for-all barrier,
/// CAS suspend/resume on the approval), real Postgres, the full command → handler → service → planner →
/// projector path, and the real DefinitionValidator are ALL real. Faked only at the honest LLM seam
/// (<see cref="DeterministicCoordinatedLlmClient"/>, ONE shared instance playing planner + coordinator). This
/// proves L3 checkpoint coordination is real end-to-end — the model re-decides BETWEEN rounds.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CoordinatedPlanFlowTests
{
    private readonly PostgresFixture _fixture;

    public CoordinatedPlanFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Coordinated_plan_re_decides_across_two_rounds_reworks_then_terminates_on_done()
    {
        Environment.SetEnvironmentVariable(WorkflowPlanningService.EnabledEnvVar, "1");
        try
        {
            var fake = ResetSharedFake();
            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

            // ── Plan a COORDINATED workflow from a task (max 5 rounds; the coordinator stops it at 2). ──
            var result = await PlanCoordinatedAsync(teamId, userId, "Improve the module across rounds", maxRounds: 5);

            result.PlannerEnabled.ShouldBeTrue();
            result.Definition.ShouldNotBeNull();
            result.Definition!.Nodes.ShouldContain(n => n.TypeKey == "flow.loop", "the coordinated projection emits a loop");

            // The projection validates independently of the service's own pre-return validation.
            Validate(result.Definition!).IsValid.ShouldBeTrue();

            // ── Persist + run. Retarget the llm.complete nodes (body + coordinator + synth) to the fake. ──
            var runnable = RetargetLlmToFake(result.Definition!);
            var workflowId = await CreateWorkflowAsync(teamId, userId, runnable);
            var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            // ── Pass 1: suspends on the plan-review approval wait. ──
            await RunEngineAsync(runId);
            await AssertSuspendedOnApprovalAsync(runId);

            // ── Approve → run to completion: round 1 (rework) → round 2 (done) → synth → Success. ──
            (await ApproveAsync(runId, teamId, userId)).ShouldBeTrue();
            await RunEngineAsync(runId);

            await AssertCoordinatedSuccessAsync(runId, fake);
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
        wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval, "the coordinated graph pauses for human plan review before the rounds begin");
    }

    private async Task AssertCoordinatedSuccessAsync(Guid runId, DeterministicCoordinatedLlmClient fake)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the coordinated loop should re-decide rework→done and the run reach Success; if not, inspect the failed WorkflowRunNode rows for this runId");

        // The loop ran EXACTLY 2 iterations (rework round 1, done round 2) — NOT the cap of 5 — and stopped on the condition.
        var loop = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "loop" && n.IterationKey == "");
        var loopOut = JsonDocument.Parse(loop.OutputsJson!).RootElement;
        loopOut.GetProperty("iterations").GetInt32().ShouldBe(2, "the coordinator decided done on round 2, so the loop ran exactly two passes (not the 5-round cap)");
        loopOut.GetProperty("terminationReason").GetString().ShouldBe("condition", "the loop stopped on the decision==done termination condition, not maxIterations");
        loopOut.GetProperty("decision").GetString().ShouldBe("done", "the final loop-var decision threaded the coordinator's round-2 verdict");

        // The coordinator decided exactly twice (once per round).
        fake.CoordinatorCalls.ShouldBe(2);

        // Round 1: the map fanned over the TWO planned subtasks.
        var round1Map = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "loop#0");
        JsonDocument.Parse(round1Map.OutputsJson!).RootElement.GetProperty("count").GetInt32()
            .ShouldBe(DeterministicCoordinatedLlmClient.PlanSubtaskTitles.Count, "round 1 fanned over the planned subtasks ({{input.subtasks}})");

        // Round 2: the loop re-seeded `subtasks` from the coordinator's reworkSubtasks, so the map fanned over the THREE rework subtasks.
        var round2Map = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "loop#1");
        JsonDocument.Parse(round2Map.OutputsJson!).RootElement.GetProperty("count").GetInt32()
            .ShouldBe(DeterministicCoordinatedLlmClient.ReworkSubtaskTitles.Count, "round 2 fanned over the coordinator's REWORK subtasks — the loop var re-seeded from {{nodes.coordinator.outputs.json.reworkSubtasks}}");

        // Prove the LOAD-BEARING re-seed, not just the width: a round-2 body branch echoed a REWORK subtask's title.
        var reworkTitle = DeterministicCoordinatedLlmClient.ReworkSubtaskTitles[0];
        var round2Body = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "body" && n.IterationKey == "loop#1/map#0");
        var bodyText = JsonDocument.Parse(round2Body.OutputsJson!).RootElement.GetProperty("text").GetString();
        bodyText.ShouldContain(reworkTitle, Case.Sensitive, $"round-2 branch must resolve {{{{item.title}}}} to the rework subtask '{reworkTitle}' — proves the loop var re-seeded the map from the coordinator's decision");

        // The synthesizer ran after the loop terminated.
        (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "synth" && n.IterationKey == "")).Status.ShouldBe(NodeStatus.Success);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Reset the shared coordinator fake's round counter so a re-run starts clean, and hand back the instance for assertions.</summary>
    private DeterministicCoordinatedLlmClient ResetSharedFake()
    {
        using var scope = _fixture.BeginScope();
        var fake = scope.Resolve<DeterministicCoordinatedLlmClient>();   // concrete type → the raw shared singleton, past the ILLMClient recording decorator
        fake.Reset();
        return fake;
    }

    private async Task<PlanWorkflowFromTaskResult> PlanCoordinatedAsync(Guid teamId, Guid userId, string taskText, int maxRounds)
    {
        // Child-scope registry holds ONLY the coordinated fake → the planner resolves it deterministically.
        using var scope = _fixture.BeginScope(b =>
        {
            RegisterCaller(b, userId, teamId);
            b.RegisterInstance(new LLMClientRegistry(new ILLMClient[] { ResolveSharedFake() })).As<ILLMClientRegistry>().SingleInstance();
        });

        return await scope.Resolve<IMediator>().Send(new PlanWorkflowFromTaskCommand
        {
            TaskText = taskText,
            Coordinated = true,
            MaxRounds = maxRounds,
        });
    }

    /// <summary>The fixture-root shared coordinated fake (the same instance the engine resolves) so the planner + coordinator share the round counter.</summary>
    private DeterministicCoordinatedLlmClient ResolveSharedFake()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<DeterministicCoordinatedLlmClient>();   // concrete type → the raw shared singleton, past the ILLMClient recording decorator
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
            Name = "coordinated-" + Guid.NewGuid().ToString("N")[..6],
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

    /// <summary>Rewrite every llm.complete node's provider to the coordinated fake's tag (preserving every other config key, incl. the coordinator's responseSchema) so the engine resolves the fake with no API key.</summary>
    private static WorkflowDefinition RetargetLlmToFake(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(RetargetNode).ToList(),
    };

    private static NodeDefinition RetargetNode(NodeDefinition node)
    {
        if (node.TypeKey != "llm.complete") return node;

        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(DeterministicCoordinatedLlmClient.ProviderTag);

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }
}
