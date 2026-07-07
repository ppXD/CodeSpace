using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Effort;
using CodeSpace.Messages.Plans;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 High fidelity — the S4d GRAPH-TIER confirm gate end to end on the STANDARD tier (real engine + real
/// <see cref="PlanConfirmNode"/> + real Action-wait resume + real WorkPlan store + real agents through the fake
/// CLI; only the planner LLM + the CLI's intelligence are deterministic fakes at their honest seams):
///
/// <list type="bullet">
///   <item>Launch-shaped projection with <c>requirePlanConfirmation</c> → the graph gains planner → confirm →
///         map; the run PARKS on the authored plan (WorkPlan v1 AwaitingConfirmation, zero agents) with the
///         <c>plan-confirm</c> wait the confirm endpoint locates without parsing the definition.</item>
///   <item>REVISION FEEDBACK through the same endpoint the Session Room checklist posts to → the node re-plans
///         (v2 with the revision origin key, v1 Rejected) and RE-PARKS on v2 — the deer-flow edit loop on a
///         static graph, no cycles.</item>
///   <item>APPROVE → v2 Confirmed and the map fans out over the CONFIRM node's outputs — the agents execute
///         the REVISED instruction, proving a rejected plan can never reach the fan-out.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class StandardPlanConfirmFanoutFlowTests
{
    private const string SeedGoal = "Improve the onboarding module across the codebase";

    private readonly PostgresFixture _fixture;

    public StandardPlanConfirmFanoutFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_standard_run_parks_on_its_plan_revises_against_feedback_then_fans_out_only_the_approved_version()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, plannerRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "workplan-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var runId = await ProjectGatedAndStartAsync(teamId, userId, plannerRowId);

        // ── Pass 1: the planner authors v1, the confirm node parks the run on it. ──
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the authored plan parks the run — no agent before the operator answers");

            var wait = await db.WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending);
            wait.NodeId.ShouldBe("confirm");
            wait.IterationKey.ShouldBe(PlanConfirmNode.IterationKeyFor(1), "the park is keyed to the version it confirms");
            JsonDocument.Parse(wait.PayloadJson!).RootElement.GetProperty("kind").GetString()
                .ShouldBe(PlanConfirmNode.WaitPayloadKind, "the suspend payload is the endpoint's discriminator — no definition parsing");

            var plan = await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == runId);
            plan.Version.ShouldBe(1);
            plan.Status.ShouldBe(WorkPlanStatuses.AwaitingConfirmation);

            (await db.AgentRun.AsNoTracking().CountAsync(a => a.TeamId == teamId)).ShouldBe(0, "fail-closed: zero agents before confirmation");
        }

        // ── The operator requests changes through the SAME endpoint the Room checklist posts to. ──
        (await ConfirmAsync(runId, teamId, userId, approve: false, feedback: "merge the two steps into one pass"))!
            .Resumed.ShouldBeTrue();

        await jobClient.WaitForPendingAsync();

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the REVISED plan re-parks — every version needs its own confirmation");

            var plans = await db.WorkPlan.AsNoTracking().Where(p => p.WorkflowRunId == runId).OrderBy(p => p.Version).ToListAsync();
            plans.Count.ShouldBe(2, "the revision is a NEW immutable version");
            plans[0].Status.ShouldBe(WorkPlanStatuses.Rejected);
            plans[1].Status.ShouldBe(WorkPlanStatuses.AwaitingConfirmation);
            plans[1].OriginKind.ShouldBe(WorkPlanOrigins.Confirm);
            plans[1].OriginKey.ShouldBe($"{PlanConfirmNode.RevisionKeyPrefix}1", "the revision origin key — the crash-replay exactly-once dedupe");
            plans[1].ItemsJson.ShouldContain(DeterministicWorkPlanLlmClient.RevisedInstruction);

            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending))
                .IterationKey.ShouldBe(PlanConfirmNode.IterationKeyFor(2), "a FRESH wait keyed to v2 — v1's resolved wait stays as history");
        }

        // ── Approve v2 → the map fans out over the CONFIRM node's approved outputs. ──
        (await ConfirmAsync(runId, teamId, userId, approve: true, feedback: null))!
            .Resumed.ShouldBeTrue();

        await jobClient.WaitForPendingAsync();

        using (var final = _fixture.BeginScope())
        {
            var db = final.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "approve released the gate and the fan-out ran to completion");

            // The version ladder is the audit trail: v1 rejected with the feedback, v2 confirmed and shipped.
            var ladder = await db.WorkPlan.AsNoTracking().Where(p => p.WorkflowRunId == runId).OrderBy(p => p.Version).Select(p => p.Status).ToListAsync();
            ladder.ShouldBe(new[] { WorkPlanStatuses.Rejected, WorkPlanStatuses.Confirmed });

            var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
            agentRuns.Count.ShouldBe(1, "the APPROVED (revised, merged) plan has one item — v1's two items never fanned out");

            var goal = JsonSerializer.Deserialize<Messages.Agents.AgentTask>(agentRuns.Single().TaskJson, Core.Services.Agents.AgentJson.Options)!.Goal;
            goal.ShouldBe(DeterministicWorkPlanLlmClient.RevisedInstruction, "the branch executed the REVISED instruction — the map bound the confirm node's approved outputs");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<WorkPlanConfirmationOutcome?> ConfirmAsync(Guid runId, Guid teamId, Guid userId, bool approve, string? feedback)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkPlanConfirmationService>().AnswerAsync(runId, teamId, userId, approve, feedback, CancellationToken.None);
    }

    /// <summary>Project the REAL plan-map-synth builder with the confirm gate ON (the launch-shaped context), pin the planner to the deterministic work-plan fake's row (both the plan.author and the confirm node's revisions resolve it), retarget the synth, and start via the real starter.</summary>
    private async Task<Guid> ProjectGatedAndStartAsync(Guid teamId, Guid userId, Guid plannerRowId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            Route = new RoutePlan { RecipeKind = TaskRecipeKinds.MapFanout, ProjectionKind = TaskProjectionKinds.PlanMapSynth, Caps = new RouteCaps() },
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
            RequirePlanConfirmation = true,
            PlannerModelRowId = plannerRowId,
        };

        var definition = RetargetSynth(scope.Resolve<ITaskProjectionRegistry>().Resolve(context.Route.ProjectionKind).Build(context));

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    /// <summary>Only the SYNTH llm.complete retargets to the plain-text fake — the planner + confirm nodes resolve the work-plan fake through the launch-baked plannerModelId pin (the production path).</summary>
    private static WorkflowDefinition RetargetSynth(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(n => n.Id == "synth" ? RetargetProvider(n, DeterministicSynthLlmClient.ProviderTag) : n).ToList(),
    };

    private static NodeDefinition RetargetProvider(NodeDefinition node, string providerTag)
    {
        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(providerTag);

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }
}
