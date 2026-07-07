using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
        jobClient.AutoExecute = true;   // the agent.code suspend runs the REAL executor + runner + fake CLI per branch

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // The planner's llm.complete resolves its model from the team POOL (S6b); seed the gateway as the team's ONLY
        // Anthropic pool model so the planner authors against CODESPACE_LLM_* with no native key.
        await SeedGatewayPoolModelAsync(teamId, BaseUrlFor(baseUrl!), apiKey!, model!);

        // Deterministic routing (NOT model-dependent) — assert the headline plan-map-synth recipe before the live call.
        var route = await RouteStandardAsync(teamId);
        route.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout);
        route.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth);

        // The live planner DECISION is the only gated-observed part; report ✅/⚠️, never red on a model miss. No
        // assertion inside drive (a wiring fault surfaces as a run Failure → a legible ⚠️, not a propagating red).
        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var runId = await ProjectRetargetSynthAndStartAsync(route, teamId, userId);

            await RunEngineAsync(runId);
            await jobClient.WaitForPendingAsync();

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
    private async Task<Guid> ProjectRetargetSynthAndStartAsync(RoutePlan route, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = PlanMapSynthPlannerRequest.Seed(teamId),
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var builder = scope.Resolve<ITaskProjectionRegistry>().Resolve(route.ProjectionKind);
        var definition = RetargetSynthToFake(builder.Build(context));

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

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
    private async Task SeedGatewayPoolModelAsync(Guid teamId, string baseUrl, string apiKey, string modelId)
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
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = Guid.NewGuid(), ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
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
