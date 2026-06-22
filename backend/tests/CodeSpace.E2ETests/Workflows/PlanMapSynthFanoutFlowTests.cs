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
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// THE crown jewel for PR5: an explicit <c>standard</c>-effort task routes through the REAL
/// <see cref="EffortRouter"/> to the map-fanout recipe → the plan-map-synth projection, whose REAL
/// <see cref="PlanMapSynthDefinitionBuilder"/> emits the planner→map→agent→synth graph, which the engine runs
/// as a SNAPSHOT run (no Workflow row, PR1's promise) to Success — fanning out to MULTIPLE real agents.
///
/// <para>The flow, as ONE pipeline: the real router (effort='standard', no requested recipe) decides
/// <c>RecipeKind=map-fanout</c> + <c>ProjectionKind=plan-map-synth</c> — the gap PR3 left open; the real builder
/// projects the graph; we persist it UNCHANGED except retargeting the planner's <c>llm.complete</c> provider to
/// the deterministic fake (the SAME honest IStructuredLLMClient-seam retarget HeadlineFlow / PlannerCodingFlow
/// use); the real <see cref="IRunFromSnapshotStarter"/> freezes + dispatches it; the engine walks
/// planner → <c>flow.map</c> (fans out one <c>agent.code</c> branch per planned subtask) → each branch ACTUALLY
/// EXECUTES through the real <see cref="IAgentRunExecutor"/> → real <c>LocalProcessRunner</c> → the
/// <see cref="SubtaskAwareFakeCli"/> process → real ParseEvent/BuildResult → natural resume; the synthesizer
/// reduces the real per-branch results.</para>
///
/// <para><b>This is a GENUINE multi-agent fan-out, not a single agent:</b> the assertions pin MORE THAN ONE
/// AgentRun (one per planned subtask), each Succeeded, and that each per-subtask goal propagated through the map
/// to its own agent (the folded summaries reflect the per-element subtask). The run reaches Success and creates
/// NO workflow / workflow_version row.</para>
///
/// <para><b>Fidelity (Rule 12) — HIGH tier on the spine.</b> The real router + recipe registry + projection
/// builder + snapshot starter (DefinitionValidator + DefinitionHash + dispatcher CAS), the real engine (the
/// wait-for-all map barrier, RehydrateMapResults), real Postgres, the AgentRunService state machine, the real
/// AgentRunExecutor, the real LocalProcessRunner SPAWNING A REAL OS PROCESS, the harness's real
/// ParseEvent/BuildResult, and the reduce are ALL real. Two things are faked at honest boundaries: the LLM at
/// the <c>IStructuredLLMClient</c> seam (<see cref="DeterministicPlannerLlmClient"/>) and the CLI's intelligence
/// at the binary (<see cref="SubtaskAwareFakeCli"/>, pinned by <see cref="SubtaskAwareFakeCliDriftTests"/>).
/// POSIX-only: the fake CLI is a /bin/sh script (Rule 12.1).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class PlanMapSynthFanoutFlowTests
{
    private readonly PostgresFixture _fixture;

    private const string SeedGoal = "Improve the onboarding module across the codebase";

    public PlanMapSynthFanoutFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Standard_effort_routes_map_fanout_and_fans_out_to_multiple_real_agents_to_success()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        // The CLI's intelligence is faked at the binary; everything the executor/runner/harness does is real.
        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.code suspend dispatches the REAL AgentRunExecutor + real runner + fake CLI

        var workflowCountBefore = await CountWorkflowsAsync(teamId);
        var versionCountBefore = await CountWorkflowVersionsAsync();

        // ── ROUTE: explicit 'standard' effort, no requested recipe → the gap-closure decision. ──
        var route = await RouteStandardAsync(teamId);

        route.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout, "explicit 'standard' routed the map-fanout recipe — the multi-agent shape");
        route.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth, "map-fanout's default projection is the planner→map→synth graph");

        // ── PROJECT + START: the real builder emits the graph; retarget ONLY the planner llm.complete to the
        //    deterministic fake (the honest LLM seam); the real starter freezes + dispatches the snapshot run. ──
        var runId = await ProjectRetargetAndStartAsync(route, teamId, userId);

        // ── Pass 1: planner emits subtasks, the map fans out N real agent.code branches, each parks + dispatches
        //    its real executor job; the run suspends. ──
        await RunEngineAsync(runId);

        // ── Drain: the real executor jobs spawn the fake CLI through the real runner, each completes for real,
        //    the completion notifier resumes, the last branch advances the map → synthesize. ──
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        await AssertRunSucceededAsSnapshotAsync(db, runId, teamId, workflowCountBefore, versionCountBefore);
        await AssertFannedOutToMultipleRealAgentsAsync(db, runId);
        await AssertEachSubtaskGoalPropagatedAsync(db, runId);
        await AssertSynthComposedRealResultsAsync(db, runId);
    }

    // ─── Assertions ──────────────────────────────────────────────────────────

    private static async Task AssertRunSucceededAsSnapshotAsync(CodeSpaceDbContext db, Guid runId, Guid teamId, int workflowCountBefore, int versionCountBefore)
    {
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the whole router→map-fanout→plan-map-synth→real-agents→synth flow must reach Success; if not, inspect the failed WorkflowRunNode rows + the AgentRun.Error for this run");

        run.WorkflowId.ShouldBeNull("a routed map-fanout task run is a snapshot run — not a child of any workflow");
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
        agentRuns.Count.ShouldBe(DeterministicPlannerLlmClient.Subtasks.Count, "one real AgentRun executed per planned subtask");
        agentRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded, "every branch agent actually executed to Succeeded via the real executor + runner");
        agentRuns.ShouldAllBe(r => r.ResultJson != null, "each run persisted a real folded AgentRunResult — not a fabricated stand-in");
        agentRuns.ShouldAllBe(r => r.NodeId == "agent", "every branch links back to the projected agent.code body node");
    }

    /// <summary>EACH subtask's goal propagated through the fan-out: every planned subtask's goal ("Work on &lt;subtask&gt;") shows up as exactly one real agent's resolved goal — proving the map's {{item}} resolved per branch.</summary>
    private static async Task AssertEachSubtaskGoalPropagatedAsync(CodeSpaceDbContext db, Guid runId)
    {
        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();

        var actualGoals = agentRuns.Select(r => JsonSerializer.Deserialize<Messages.Agents.AgentTask>(r.TaskJson, AgentJson.Options)!.Goal).OrderBy(g => g).ToList();
        var expectedGoals = DeterministicPlannerLlmClient.Subtasks.Select(s => $"Work on {s}").OrderBy(g => g).ToList();

        actualGoals.ShouldBe(expectedGoals,
            customMessage: "each agent's resolved goal must be 'Work on <subtask>' for the planner's OWN subtasks — proving the map's {{item}} binding resolved per branch and the goal propagated through the fan-out");
    }

    /// <summary>The synthesizer ran a REAL llm.complete REDUCE over the WHOLE map results array (NOT a raw-array re-bind): the run's combined output is the deterministic synth client's transform of a prompt that embeds the goal AND every per-branch summary — proving the synth READ all branches, generic over the subtask count.</summary>
    private static async Task AssertSynthComposedRealResultsAsync(CodeSpaceDbContext db, Guid runId)
    {
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        var combined = JsonDocument.Parse(run.OutputsJson).RootElement.GetProperty("combined");
        combined.ValueKind.ShouldBe(JsonValueKind.String, "the synth is a REAL llm.complete reduce now — its text output is one string, not the raw {{nodes.map.outputs.results}} array the old builtin.terminal passed through");

        var reduced = combined.GetString()!;
        reduced.ShouldStartWith(DeterministicSynthLlmClient.Prefix,
            customMessage: "the combined output is the deterministic synth client's REDUCE — a raw-array re-bind could never carry this marker, proving an llm.complete node ran");

        // The reduce prompt embeds the goal AND the WHOLE per-branch results array (each carrying its real
        // fake-CLI-derived summary). The deterministic synth echoes its prompt, so EVERY subtask's real summary
        // must be present in the reduced output — proving the synth read all fanned-out branches, not a first-N.
        foreach (var subtask in DeterministicPlannerLlmClient.Subtasks)
        {
            var expectedSummary = SubtaskAwareFakeCli.ExpectedSummaryFor($"Work on {subtask}");
            reduced.ShouldContain(expectedSummary,
                customMessage: $"the synth reduce must have read subtask '{subtask}'s real folded summary from the results array — its absence means the reduce didn't see all branches");
        }

        reduced.ShouldContain(SeedGoal,
            customMessage: "the reduce prompt embeds the seed goal — proving the synth addresses the goal, not just concatenates branches");

        var mapNode = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "map" && n.IterationKey == "");
        var mapOut = JsonDocument.Parse(mapNode.OutputsJson!).RootElement;
        mapOut.GetProperty("count").GetInt32().ShouldBe(DeterministicPlannerLlmClient.Subtasks.Count);
        mapOut.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<RoutePlan> RouteStandardAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var request = new EffortRouteRequest
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            RequestedEffort = TaskEffortModes.Standard,
        };

        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    /// <summary>Build via the REAL projection builder (resolved by the route's kind), retarget ONLY the planner llm.complete to the deterministic fake, then start the snapshot run via the REAL starter.</summary>
    private async Task<Guid> ProjectRetargetAndStartAsync(RoutePlan route, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = new TaskLaunchSeed { Goal = SeedGoal, SurfaceKind = "test", TeamId = teamId },
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var builder = scope.Resolve<ITaskProjectionRegistry>().Resolve(route.ProjectionKind);

        var definition = RetargetLlmNodesToFakes(builder.Build(context));

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, CancellationToken.None);
    }

    /// <summary>Test-only adaptation: rewrite the BOTH <c>llm.complete</c> providers — the PLANNER node to the structured planner fake, the SYNTH node to the plain-text synth fake — so the engine resolves the deterministic fakes (no API key). Retarget is BY NODE ID (the graph now has two llm.complete nodes); the agent.code body + the graph SHAPE are left exactly as the production builder emitted them.</summary>
    private static WorkflowDefinition RetargetLlmNodesToFakes(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(RetargetNode).ToList(),
    };

    private static NodeDefinition RetargetNode(NodeDefinition node) => node.Id switch
    {
        "planner" => RetargetProvider(node, DeterministicPlannerLlmClient.ProviderTag),
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
