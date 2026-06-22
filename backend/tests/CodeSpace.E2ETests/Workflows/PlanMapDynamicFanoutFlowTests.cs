using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// THE crown jewel for PR-B: the MODEL authors a per-agent SPEC and that decision DRIVES the platform. An explicit
/// <c>RequestedRecipe="map-fanout-dynamic"</c> routes through the REAL <see cref="EffortRouter"/> to the
/// plan-map-dynamic projection (proving the opt-in recipe is reachable), whose REAL
/// <see cref="Core.Services.Tasks.Projection.Builders.PlanMapDynamic.PlanMapDynamicDefinitionBuilder"/> emits the
/// planner→map→agent→synth graph, which the engine runs as a SNAPSHOT run (no Workflow row) to Success — fanning
/// out to MULTIPLE real agents whose PERMISSIONS + PUSH posture come from the planner's per-subtask
/// <c>mode</c> choice.
///
/// <para>The spec planner (<see cref="DeterministicSpecPlannerLlmClient"/>) authors a HETEROGENEOUS plan: one
/// <c>research</c> subtask ("Work on alpha") + two <c>code</c> subtasks ("Work on beta", "Work on gamma"). The map
/// binds <c>{{item.goal}}</c> + <c>{{item.mode}}</c> per branch; the agent.code node maps each mode to a base
/// (research = read-only + no produced branch; code = workspace write + push its own branch) UNDER the
/// autonomy-tier + override precedence. The assertions read the PERSISTED <c>AgentRun.TaskJson</c> → <c>AgentTask</c>
/// and prove the model→platform DECISION per branch: the research branch resolved to <c>WriteScope=ReadOnly</c> +
/// <c>PushProducedBranch=false</c>, the code branches to <c>WriteScope=Workspace</c> + <c>PushProducedBranch=true</c>.</para>
///
/// <para><b>The env push flag is OFF for the whole test.</b> The fan-out profile has NO RepositoryId (analysis-only,
/// no workspace) → the workspace is not an <c>IWorkspacePushHandle</c> → the executor's push step returns early
/// regardless. So this test asserts the model→platform DECISION (per-branch <c>AgentTask.PushProducedBranch</c> +
/// <c>Permissions.WriteScope</c>), NOT a real remote branch — by design.</para>
///
/// <para><b>Fidelity (Rule 12) — HIGH tier on the spine.</b> The real router + recipe registry + projection builder
/// + snapshot starter (DefinitionValidator + DefinitionHash + dispatcher CAS), the real engine (map barrier,
/// RehydrateMapResults), real Postgres, the AgentRunService state machine, the real AgentRunExecutor, the real
/// LocalProcessRunner SPAWNING A REAL OS PROCESS, the harness's real ParseEvent/BuildResult, and the reduce are
/// ALL real. Two things are faked at honest boundaries: the LLM at the <c>IStructuredLLMClient</c> seam
/// (<see cref="DeterministicSpecPlannerLlmClient"/>, a SEPARATE fake so the string-planner stays untouched) and the
/// CLI's intelligence at the binary (<see cref="SubtaskAwareFakeCli"/>). POSIX-only: the fake CLI is a /bin/sh
/// script (Rule 12.1).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class PlanMapDynamicFanoutFlowTests
{
    private readonly PostgresFixture _fixture;

    private const string SeedGoal = "Improve the onboarding module across the codebase";

    public PlanMapDynamicFanoutFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Dynamic_fanout_maps_each_planner_authored_mode_to_per_branch_permissions_and_push()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        // The asserted PushProducedBranch is written by the NODE's mode→push mapping at suspend time (into the
        // persisted AgentRun.TaskJson this test reads), and the dynamic builder binds NO explicit pushBranch config
        // — so a branch's MODE is the SOLE driver of that persisted value (code→true, research→false). The
        // deployment push flag governs only whether a REAL push FIRES (not asserted here — the profile has no repo,
        // so no push handle exists); keep it OFF purely as defense-in-depth, so nothing about a real push could be
        // mistaken for the model's mode decision. (It is NOT what enforces this assertion.)
        var originalPushFlag = Environment.GetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar);
        Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, null);

        try
        {
            // The CLI's intelligence is faked at the binary; everything the executor/runner/harness does is real.
            using var cli = new SubtaskAwareFakeCli();

            var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

            var jobClient = ResolveJobClient();
            jobClient.Clear();
            jobClient.AutoExecute = true;   // the agent.code suspend dispatches the REAL AgentRunExecutor + real runner + fake CLI

            var workflowCountBefore = await CountWorkflowsAsync(teamId);
            var versionCountBefore = await CountWorkflowVersionsAsync();

            // ── ROUTE: explicit standard effort + an explicit map-fanout-dynamic recipe pin → proves the opt-in
            //    dynamic recipe is reachable (it serves no tier; only an explicit RequestedRecipe routes it). ──
            var route = await RouteDynamicAsync(teamId);

            route.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanoutDynamic, "the explicit RequestedRecipe pinned the opt-in model-authored recipe");
            route.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapDynamic, "map-fanout-dynamic's default projection is the model-authored planner→map→synth graph");

            // ── PROJECT + START: the real builder emits the graph; retarget the planner llm.complete to the SPEC
            //    planner fake + the synth to the synth fake; the real starter freezes + dispatches the snapshot run. ──
            var runId = await ProjectRetargetAndStartAsync(route, teamId, userId);

            // ── Pass 1: planner emits per-agent specs, the map fans out N real agent.code branches, each parks +
            //    dispatches its real executor job; the run suspends. ──
            await RunEngineAsync(runId);

            // ── Drain: the real executor jobs spawn the fake CLI through the real runner, each completes for real,
            //    the completion notifier resumes, the last branch advances the map → synthesize. ──
            await jobClient.WaitForPendingAsync();

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            await AssertRunSucceededAsSnapshotAsync(db, runId, teamId, workflowCountBefore, versionCountBefore);
            await AssertFannedOutToMultipleRealAgentsAsync(db, runId);
            await AssertEachAuthoredGoalPropagatedAsync(db, runId);
            await AssertPlannerAuthoredModeDrovePerBranchPermissionsAsync(db, runId);
            await AssertSynthComposedRealResultsAsync(db, runId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentRunExecutor.PushEnabledEnvVar, originalPushFlag);
        }
    }

    // ─── Assertions ──────────────────────────────────────────────────────────

    private static async Task AssertRunSucceededAsSnapshotAsync(CodeSpaceDbContext db, Guid runId, Guid teamId, int workflowCountBefore, int versionCountBefore)
    {
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the whole router→map-fanout-dynamic→plan-map-dynamic→real-agents→synth flow must reach Success; if not, inspect the failed WorkflowRunNode rows + the AgentRun.Error for this run");

        run.WorkflowId.ShouldBeNull("a routed map-fanout-dynamic task run is a snapshot run — not a child of any workflow");
        run.WorkflowVersion.ShouldBeNull("a snapshot run has no pinned version");
        run.DefinitionSnapshotJson.ShouldNotBeNull("the projected definition is frozen inline on the run");

        (await db.Workflow.AsNoTracking().CountAsync(w => w.TeamId == teamId)).ShouldBe(workflowCountBefore, "no workflow row is created for a snapshot run");
        (await db.WorkflowVersion.AsNoTracking().CountAsync()).ShouldBe(versionCountBefore, "no workflow_version row is created for a snapshot run");
    }

    /// <summary>The fan-out is GENUINELY multi-agent: one real AgentRun per planned subtask (MORE THAN ONE), each Succeeded with a real folded result.</summary>
    private static async Task AssertFannedOutToMultipleRealAgentsAsync(CodeSpaceDbContext db, Guid runId)
    {
        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();

        agentRuns.Count.ShouldBeGreaterThan(1, "the map fanned out to MULTIPLE agents — this is a real multi-agent run, not a single agent");
        agentRuns.Count.ShouldBe(DeterministicSpecPlannerLlmClient.Specs.Count, "one real AgentRun executed per planned subtask");
        agentRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded, "every branch agent actually executed to Succeeded via the real executor + runner");
        agentRuns.ShouldAllBe(r => r.NodeId == "agent", "every branch links back to the projected agent.code body node");
    }

    /// <summary>EACH authored subtask GOAL propagated through the fan-out: every planner-authored goal shows up as exactly one real agent's resolved goal — proving the map's {{item.goal}} resolved per branch.</summary>
    private static async Task AssertEachAuthoredGoalPropagatedAsync(CodeSpaceDbContext db, Guid runId)
    {
        var tasks = await AgentTasksFor(db, runId);

        var actualGoals = tasks.Select(t => t.Goal).OrderBy(g => g).ToList();
        var expectedGoals = DeterministicSpecPlannerLlmClient.Specs.Select(s => s.Goal).OrderBy(g => g).ToList();

        actualGoals.ShouldBe(expectedGoals,
            customMessage: "each agent's resolved goal must be the planner's OWN authored subtask goal — proving the map's {{item.goal}} binding resolved per branch and the goal propagated through the fan-out");
    }

    /// <summary>
    /// THE crown assertion — the model→platform DECISION, per branch. The planner authored a heterogeneous plan
    /// (one research + two code); the node mapped each authored <c>mode</c> to a permission + push posture. Read
    /// from the PERSISTED <c>AgentRun.TaskJson</c> → <c>AgentTask</c>: the research branch resolved to
    /// <c>WriteScope=ReadOnly</c> + <c>PushProducedBranch=false</c> (analysis-only, no branch), the code branches to
    /// <c>WriteScope=Workspace</c> + <c>PushProducedBranch=true</c> (each publishes its own branch). The dynamic
    /// builder binds NO explicit pushBranch, so the persisted <c>PushProducedBranch</c> is purely the node's mapping
    /// of the planner's <c>mode</c> (code→true, research→false) — the model's decision.
    ///
    /// <para>The discriminating signal differs per side: the profile is <c>Trusted</c> (whose tier baseline is
    /// <c>WriteScope=Workspace</c>), so the research branch resolving to <c>ReadOnly</c> proves <c>mode=research</c>
    /// OVERRODE the tier (a mode-driven write-scope decision); for the code branches <c>Workspace</c> equals the tier
    /// baseline (mode=code never raises the tier — clamp-safe), so THEIR mode-driven signal is <c>PushProducedBranch=true</c>.</para>
    /// </summary>
    private static async Task AssertPlannerAuthoredModeDrovePerBranchPermissionsAsync(CodeSpaceDbContext db, Guid runId)
    {
        // Baseline contrast: the profile's Trusted tier alone derives Workspace write — so the research branch's
        // ReadOnly below can ONLY be mode=research overriding the tier, not the tier's own posture.
        AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted).WriteScope.ShouldBe(AgentWriteScope.Workspace,
            customMessage: "the Trusted tier baseline is Workspace write — so a research branch resolving to ReadOnly is provably the mode override, not the tier");

        var byGoal = (await AgentTasksFor(db, runId)).ToDictionary(t => t.Goal);

        foreach (var spec in DeterministicSpecPlannerLlmClient.Specs)
        {
            var task = byGoal[spec.Goal];

            if (spec.Mode == "research")
            {
                task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly,
                    customMessage: $"the planner tagged '{spec.Goal}' as research → the node resolved a READ-ONLY agent, OVERRIDING the Trusted tier's Workspace baseline (the mode-driven write-scope decision)");
                task.PushProducedBranch.ShouldBe(false,
                    customMessage: $"a research branch ('{spec.Goal}') produces no branch — the node maps mode=research → PushProducedBranch=false");
            }
            else
            {
                task.Permissions.WriteScope.ShouldBe(AgentWriteScope.Workspace,
                    customMessage: $"a code branch ('{spec.Goal}') resolves to Workspace write (here equal to the Trusted tier baseline — code never raises the tier; its mode-driven signal is the push below)");
                task.PushProducedBranch.ShouldBe(true,
                    customMessage: $"a code branch ('{spec.Goal}') publishes its own branch — PushProducedBranch=true is purely the node's mapping of the planner's mode=code (the builder binds no explicit pushBranch, so mode is the sole driver)");
            }
        }
    }

    /// <summary>The synthesizer ran a REAL llm.complete REDUCE over the WHOLE map results array: the run's combined output is the deterministic synth client's transform of a prompt that embeds the goal AND every per-branch summary — proving the synth READ all branches, generic over the subtask count.</summary>
    private static async Task AssertSynthComposedRealResultsAsync(CodeSpaceDbContext db, Guid runId)
    {
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var combined = JsonDocument.Parse(run.OutputsJson).RootElement.GetProperty("combined");
        combined.ValueKind.ShouldBe(JsonValueKind.String, "the synth is a REAL llm.complete reduce — its text output is one string, not the raw {{nodes.map.outputs.results}} array");

        var reduced = combined.GetString()!;
        reduced.ShouldStartWith(DeterministicSynthLlmClient.Prefix,
            customMessage: "the combined output is the deterministic synth client's REDUCE — proving an llm.complete node ran");

        foreach (var spec in DeterministicSpecPlannerLlmClient.Specs)
        {
            var expectedSummary = SubtaskAwareFakeCli.ExpectedSummaryFor(spec.Goal);
            reduced.ShouldContain(expectedSummary,
                customMessage: $"the synth reduce must have read subtask '{spec.Goal}'s real folded summary from the results array — its absence means the reduce didn't see all branches");
        }

        reduced.ShouldContain(SeedGoal,
            customMessage: "the reduce prompt embeds the seed goal — proving the synth addresses the goal, not just concatenates branches");

        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var mapOut = JsonDocument.Parse(mapNode.OutputsJson!).RootElement;
        mapOut.GetProperty("count").GetInt32().ShouldBe(DeterministicSpecPlannerLlmClient.Specs.Count);
        mapOut.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<List<AgentTask>> AgentTasksFor(CodeSpaceDbContext db, Guid runId)
    {
        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();

        return agentRuns.Select(r => JsonSerializer.Deserialize<AgentTask>(r.TaskJson, AgentJson.Options)!).ToList();
    }

    private async Task<RoutePlan> RouteDynamicAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var request = new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Standard,           // explicit effort → no classifier, no confirm card (deterministic)
            RequestedRecipe = TaskRecipeKinds.MapFanoutDynamic,   // the opt-in pin — proves the dynamic recipe is reachable
        };

        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    /// <summary>Build via the REAL projection builder (resolved by the route's kind), retarget BOTH llm.complete nodes to the deterministic fakes (planner→spec fake, synth→synth fake), then start the snapshot run via the REAL starter.</summary>
    private async Task<Guid> ProjectRetargetAndStartAsync(RoutePlan route, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Trusted" },
        };

        var builder = scope.Resolve<ITaskProjectionRegistry>().Resolve(route.ProjectionKind);

        var definition = RetargetLlmNodesToFakes(builder.Build(context));

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, CancellationToken.None);
    }

    /// <summary>Test-only adaptation: rewrite BOTH <c>llm.complete</c> providers — the PLANNER node to the SPEC planner fake, the SYNTH node to the plain-text synth fake — so the engine resolves the deterministic fakes (no API key). Retarget is BY NODE ID; the agent.code body + the graph SHAPE are left exactly as the production builder emitted them.</summary>
    private static WorkflowDefinition RetargetLlmNodesToFakes(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(RetargetNode).ToList(),
    };

    private static NodeDefinition RetargetNode(NodeDefinition node) => node.Id switch
    {
        "planner" => RetargetProvider(node, DeterministicSpecPlannerLlmClient.ProviderTag),
        "synth" => RetargetProvider(node, DeterministicSynthLlmClient.ProviderTag),
        _ => node,
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

    private async Task<int> CountWorkflowsAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Workflow.AsNoTracking().CountAsync(w => w.TeamId == teamId);
    }

    private async Task<int> CountWorkflowVersionsAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowVersion.AsNoTracking().CountAsync();
    }
}
