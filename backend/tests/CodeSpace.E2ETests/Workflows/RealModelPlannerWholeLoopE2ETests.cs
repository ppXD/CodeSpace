using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE real-model PLANNER gate — the headline "planner → flow.map → agents → synthesizer" dynamic-dispatch BRAIN,
/// driven by a LIVE model end-to-end on a CI lane. The sessions audit found that a real model AUTHORING the
/// decomposition was verified NOWHERE: every plan-map-synth E2E retargets the planner to a deterministic fake, and the
/// one real-model planner test (<c>RealModelPhaseAuthorshipFlowTests</c>) is orphaned — its LIVE arm keys on a native
/// Anthropic key bound in no workflow and matches no lane filter, its REPLAY arm has no committed cassette.
///
/// <para>This closes that gap WITHOUT a human-recorded cassette: it leans on the pool-driven <c>llm.complete</c>
/// (the planner resolves its model + credential from the team's credentialed-model POOL — <see cref="LlmCompleteNode"/>
/// S6b), so seeding ONE gateway pool model points the planner at the existing <c>CODESPACE_LLM_*</c> gateway secrets.
/// The REAL <see cref="PlanMapSynthDefinitionBuilder"/> projects the production planner→map→agent→synth graph; only the
/// SYNTH reduce is retargeted to a deterministic fake (so the test spends ONE live call, on the decision under test —
/// the planner). A live model authors the subtasks, the real durable engine fans out over the MODEL-authored width,
/// real agents (a fake CLI body) execute each branch, and the run reaches Success.</para>
///
/// <para>REPORT-ONLY (<see cref="RealModelGate.AssessLiveAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, bool)"/>
/// with <c>gating:false</c>): a real planner's decomposition is non-deterministic, so this OBSERVES whether a live model
/// authored a plan that drove the fan-out to Success and reports ✅/⚠️ to the job summary; a gateway-transport outage is
/// a non-gating infra skip. The deterministic plan-map-synth spine is already gated on backend-e2e. Self-skips (NOT a
/// pass) when <c>CODESPACE_LLM_*</c> are absent. Routed to the real-model whole-loop lane (Postgres + secrets) by the
/// <c>RealModelPlanner</c> name token.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelPlannerWholeLoopE2ETests
{
    private const string Provider = "Anthropic";   // the blessed brain wire; the planner node's default provider

    private readonly PostgresFixture _fixture;

    public RealModelPlannerWholeLoopE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_live_model_authors_the_plan_and_the_engine_fans_out_over_the_model_authored_subtasks()
    {
        var baseUrl = Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => v is not null);
        if (present == 0) { RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live planner)"); return; }   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip the blessed gate proving nothing.");

        if (OperatingSystem.IsWindows()) return;   // the fake-CLI agent body is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the agent.run suspend runs the REAL executor + runner + fake CLI per branch

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // The planner's llm.complete resolves its model from the team POOL (S6b); seed the gateway as the team's ONLY
        // Anthropic pool model so the planner authors against CODESPACE_LLM_* with no native key.
        var plannerRowId = await SeedGatewayPoolModelAsync(teamId, BaseUrlFor(baseUrl!), apiKey!, model!);

        // Deterministic routing (NOT model-dependent) — assert the headline plan-map-synth recipe before the live call.
        var route = await RouteStandardAsync(teamId);
        route.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout);
        route.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth);

        // WIRING is gated; PLAN QUALITY is not. The two were conflated behind one report-only gate, and the cost was
        // total: with no model pinned, LlmWorkflowPlanner falls to InProcessStructuredModel.ResolveAsync, which takes
        // the FIRST structured client holding a pool model — and WorkflowsTestSeed seeds one per in-process fake, the
        // first being TestPlanner, whose canned plan is a bare string array. So this "live planner" gate resolved a
        // FAKE, its output failed the production DTO bind, and gating:false reported the wreck as green. It ran in
        // 221ms; a gateway round trip cannot.
        var runId = await ProjectRetargetSynthAndStartAsync(route, teamId, userId, plannerRowId);

        var startedAt = DateTimeOffset.UtcNow;
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        await AssertTheLivePlannerActuallyRanAsync(runId, teamId, model!, elapsed);

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId)
                .Select(r => r.Status).ToListAsync();

            var fanned = agentRuns.Count;                                            // = the MODEL-authored subtask count
            var allBranchesRan = fanned > 0 && agentRuns.All(s => s == AgentRunStatus.Succeeded);
            var drove = run.Status == WorkflowRunStatus.Success && allBranchesRan;

            return (drove,
                $"{Provider} '{model}' PLANNER authored {fanned} subtask(s) → run={run.Status}, branch-agents={fanned} all-succeeded={allBranchesRan}. "
              + (drove ? "DROVE — a real model authored a decomposition the real engine fanned out + executed end to end." : "did NOT drive (reported, not gating)."));
        }, gating: false);
    }

    // ── Helpers (mirror RealModelPhaseAuthorshipFlowTests' real projection drive) ───────────────────────────────────

    private async Task<RoutePlan> RouteStandardAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var request = new EffortRouteRequest { Seed = PlanMapSynthPlannerRequest.Seed(teamId), RequestedEffort = TaskEffortModes.Standard };
        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    /// <summary>Build via the REAL plan-map-synth builder, retarget ONLY the SYNTH reduce to the deterministic fake (so just the PLANNER hits the live gateway), then start the snapshot run via the REAL starter. The planner node keeps its default Anthropic provider + no model pin → it resolves the seeded gateway pool model.</summary>
    private async Task<Guid> ProjectRetargetSynthAndStartAsync(RoutePlan route, Guid teamId, Guid userId, Guid plannerRowId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = PlanMapSynthPlannerRequest.Seed(teamId),
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var builder = scope.Resolve<ITaskProjectionRegistry>().Resolve(route.ProjectionKind);
        var definition = PinPlannerModel(RetargetSynthToFake(builder.Build(context)), plannerRowId);

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    /// <summary>
    /// PIN the planner to an exact credentialed-model row. Without this the planner takes the auto path
    /// (<c>InProcessStructuredModel.ResolveAsync</c>), which returns the first structured client that has any pool
    /// model — and the seeded team has one per in-process fake. The fake wins, and the gate measures it.
    ///
    /// <para>This is a QUALIFICATION-tier pin, not a change to production semantics: an operator leaving the planner
    /// model empty still gets the auto pick, which is the intended product behaviour. A gate that claims to measure a
    /// specific model simply may not leave the choice to whoever the registry enumerates first.</para>
    /// </summary>
    private static WorkflowDefinition PinPlannerModel(WorkflowDefinition definition, Guid plannerRowId) => definition with
    {
        Nodes = definition.Nodes.Select(n => n.TypeKey != "plan.author" ? n : n with { Config = WithKey(n.Config, "plannerModelId", plannerRowId.ToString()) }).ToList(),
    };

    private static JsonElement WithKey(JsonElement config, string key, string value)
    {
        var bag = config.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.GetRawText())!
            : new Dictionary<string, JsonElement>();

        bag[key] = JsonSerializer.SerializeToElement(value);
        return JsonSerializer.SerializeToElement(bag);
    }

    /// <summary>
    /// The gated half: did a LIVE planner actually run? Every assertion here is a code/wiring fact, so each one is a
    /// hard red — none of them can be moved by a model having a bad day. Plan QUALITY stays report-only afterwards.
    /// </summary>
    private async Task AssertTheLivePlannerActuallyRanAsync(Guid runId, Guid teamId, string expectedModel, TimeSpan elapsed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var planner = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "planner");

        planner.Status.ShouldNotBe(NodeStatus.Failure,
            $"the planner node FAILED — that is a wiring or contract fault, never a model-quality outcome (error: {planner.Error ?? "(none)"})");

        // The effective model, read back off the planner's own output. LlmWorkflowPlanner stamps `pick.ModelId` onto
        // the plan it returns, so this is what the run ACTUALLY reasoned on — not what the test hoped it would.
        var effective = JsonDocument.Parse(planner.OutputsJson).RootElement
            .GetProperty("json").GetProperty("model").GetString();

        effective.ShouldBe(expectedModel,
            $"the planner resolved '{effective}' instead of the pinned live gateway model — a gate that measures whichever client the registry enumerated first measures nothing");

        // A gateway round trip cannot complete in milliseconds. This is the assertion that would have caught the
        // fake outright: the wreck it reported as green ran the whole engine, planner included, in 221ms.
        elapsed.ShouldBeGreaterThan(LiveCallFloor,
            $"the whole run took {elapsed.TotalMilliseconds:0}ms — too fast for a live gateway call, so the planner was almost certainly served by an in-process fake");
    }

    /// <summary>Below this, no network round trip happened. Deliberately far under any real latency so it can only ever catch a fake, never a fast day.</summary>
    private static readonly TimeSpan LiveCallFloor = TimeSpan.FromMilliseconds(400);

    /// <summary>Retarget ONLY the synth node's provider to the deterministic synth fake — the planner stays the real model (pool→gateway). Mirrors RealModelPhaseAuthorshipFlowTests' synth retarget, but leaves the planner alone.</summary>
    private static WorkflowDefinition RetargetSynthToFake(WorkflowDefinition definition) => definition with
    {
        Nodes = definition.Nodes.Select(n => n.Id == "synth" ? RetargetProvider(n, DeterministicSynthLlmClient.ProviderTag) : n).ToList(),
    };

    private static NodeDefinition RetargetProvider(NodeDefinition node, string providerTag)
    {
        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["provider"] = JsonSerializer.SerializeToElement(providerTag);
        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }

    /// <summary>Seed the gateway as the team's ONLY Anthropic pool model (the planner llm.complete resolves it via the pool). Mirrors the supervisor real-model tests' SeedBrainModelAsync.</summary>
    private async Task<Guid> SeedGatewayPoolModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "live planner gateway cred",
            EncryptedApiKey = encryptor.Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
        return rowId;
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

    private static string? Env(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? null : Environment.GetEnvironmentVariable(name);

    /// <summary>Anthropic's client appends <c>/v1/messages</c> to the host base — pass the gateway host as-is.</summary>
    private static string BaseUrlFor(string baseUrl) => baseUrl.TrimEnd('/');
}
