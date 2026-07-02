using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// THE S0a kill-gate: a plan-map-synth run whose PLANNER is a REAL model (recorded/replayed via
/// <see cref="RecordReplayStructuredLLMClient"/> over the production <see cref="AnthropicClient"/>), driving the
/// same router→builder→snapshot-starter→engine→fan-out spine as <c>PlanMapSynthFanoutFlowTests</c>. The
/// difference is the decision authorship that is under test: the fan-out width + subtask content come from a
/// real-model transcript, NOT the hand-written <see cref="DeterministicPlannerLlmClient"/> fake. The AGENT
/// bodies stay <see cref="SubtaskAwareFakeCli"/> — only the planner is real/recorded.
///
/// <para><b>This closes the measure-first gap.</b> Every other planner/decider test fakes the brain at the
/// <c>IStructuredLLMClient</c> seam; here at least ONE test wires the REAL client, end-to-end through the
/// durable engine, so decision quality is exercised on a genuine model rather than asserted nowhere.</para>
///
/// <para><b>Two honestly-gated variants — what runs WHEN:</b></para>
/// <list type="bullet">
///   <item><b>LIVE</b> (<c>Trait Category=RealModel</c>): early-returns when <see cref="AnthropicClient.ApiKeyEnvVar"/>
///   is absent. With a key it calls the REAL model AND records/updates the committed cassette. CI lanes set NO
///   key → it skips in CI, which is correct and honest. An implementing agent CANNOT capture a genuine cassette
///   (no key, no egress); a human runs this with a key to record one.</item>
///   <item><b>REPLAY</b>: early-returns when no committed cassette exists yet, so it is green NOW and ACTIVATES
///   the moment a human records + commits a cassette. When a cassette exists it runs deterministically from it,
///   no API key needed — making the real-model decision a reproducible fixture in CI.</item>
/// </list>
///
/// <para><b>Fidelity (Rule 12) — HIGH on the spine</b> (real router/builder/starter/engine/executor/runner +
/// real Postgres + the real <c>AnthropicClient</c> wrapped by record/replay). POSIX-only: the fake CLI is a
/// /bin/sh script (Rule 12.1).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RealModelPhaseAuthorshipFlowTests
{
    private readonly PostgresFixture _fixture;

    public RealModelPhaseAuthorshipFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>LIVE: calls the REAL Anthropic model + records the cassette. Skips (early return) with no API key — i.e. always in CI + the sandbox. Tagged RealModel so a keyed human can target it.</summary>
    [Fact]
    [Trait("Category", "RealModel")]
    public async Task Live_real_model_authors_the_plan_and_records_the_cassette()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AnthropicClient.ApiKeyEnvVar))) return;   // no key → skip; honest CI/sandbox behaviour
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        await DriveRealModelPlanMapSynthToSuccessAsync();
    }

    /// <summary>REPLAY: runs from the committed cassette, no key. Skips (early return) until a human records one — green now, ACTIVE on cassette commit.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Replay_runs_the_recorded_real_model_plan_deterministically()
    {
        if (!RecordReplayStructuredLLMClient.CassetteExists(RealModelCassettePaths.PlannerCassettePath)) return;   // no cassette yet → skip; activates on human record
        if (OperatingSystem.IsWindows()) return;

        await DriveRealModelPlanMapSynthToSuccessAsync();
    }

    /// <summary>The shared body — identical drive/drain to <c>PlanMapSynthFanoutFlowTests</c>, but the planner is the real/recorded model and the assertions pin against the MODEL-authored subtasks read back from the cassette.</summary>
    private async Task DriveRealModelPlanMapSynthToSuccessAsync()
    {
        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var route = await RouteStandardAsync(teamId);

        route.RecipeKind.ShouldBe(TaskRecipeKinds.MapFanout);
        route.ProjectionKind.ShouldBe(TaskProjectionKinds.PlanMapSynth);

        var runId = await ProjectRetargetToRealModelAndStartAsync(route, teamId, userId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var modelSubtasks = await ReadModelAuthoredSubtasksAsync(teamId);

        await AssertRunSucceededAsync(db, runId);
        await AssertFannedOutOverModelAuthoredSubtasksAsync(db, runId, modelSubtasks);
    }

    // ─── Assertions ──────────────────────────────────────────────────────────

    private static async Task AssertRunSucceededAsync(CodeSpaceDbContext db, Guid runId)
    {
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the real-model planner → map fan-out → fake-CLI agents → synth flow must reach Success; if not, inspect the failed WorkflowRunNode rows + AgentRun.Error for this run");

        run.WorkflowId.ShouldBeNull("a routed map-fanout task run is a snapshot run — not a child of any workflow");
        run.DefinitionSnapshotJson.ShouldNotBeNull("the projected definition is frozen inline on the run");
    }

    /// <summary>The fan-out width + per-branch goals match the subtasks the REAL MODEL authored (read back from the cassette) — proving the decomposition that drove the fan-out came from a genuine model transcript, not a hand-written fake.</summary>
    private static async Task AssertFannedOutOverModelAuthoredSubtasksAsync(CodeSpaceDbContext db, Guid runId, IReadOnlyList<string> modelSubtasks)
    {
        modelSubtasks.Count.ShouldBeGreaterThan(0, "the recorded real-model plan must contain at least one subtask");

        var agentRuns = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();

        agentRuns.Count.ShouldBe(modelSubtasks.Count, "one real AgentRun executed per MODEL-authored subtask — the fan-out width came from the real-model transcript");
        agentRuns.ShouldAllBe(r => r.Status == AgentRunStatus.Succeeded, "every branch agent executed to Succeeded via the real executor + runner");

        var actualGoals = agentRuns.Select(r => JsonSerializer.Deserialize<Messages.Agents.AgentTask>(r.TaskJson, AgentJson.Options)!.Goal).OrderBy(g => g).ToList();
        var expectedGoals = modelSubtasks.OrderBy(g => g).ToList();

        actualGoals.ShouldBe(expectedGoals,
            customMessage: "each agent's resolved goal must be the MODEL's own authored instruction — proving the plan.author decomposition propagated through the map's per-branch {{item.instruction}} binding");
    }

    /// <summary>Read the subtask INSTRUCTIONS the model authored back out of the committed cassette — plan.author's subtasks are objects, and the map's per-branch goal binds each item's <c>instruction</c>.</summary>
    private async Task<IReadOnlyList<string>> ReadModelAuthoredSubtasksAsync(Guid teamId)
    {
        StructuredLLMCompletionRequest request;
        using (var scope = _fixture.BeginScope())
            request = await PlanMapSynthPlannerRequest.BuildAsync(scope, teamId, CancellationToken.None);

        var entries = JsonSerializer.Deserialize<List<RecordReplayStructuredLLMClient.CassetteEntry>>(File.ReadAllText(RealModelCassettePaths.PlannerCassettePath))!;

        var key = RecordReplayStructuredLLMClient.CassetteKey(request);
        var entry = entries.FirstOrDefault(e => e.KeyHash == key)
            ?? throw new InvalidOperationException($"Cassette has no entry for the plan-map-synth planner key {key} — re-record via the RealModel live test.");

        return entry.JsonElement().GetProperty("subtasks").EnumerateArray().Select(s => s.GetProperty("instruction").GetString()!).ToList();
    }

    // ─── Helpers (mirror PlanMapSynthFanoutFlowTests) ──────────────────────────

    private async Task<RoutePlan> RouteStandardAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var request = new EffortRouteRequest
        {
            Seed = PlanMapSynthPlannerRequest.Seed(teamId),
            RequestedEffort = TaskEffortModes.Standard,
        };

        return await scope.Resolve<IEffortRouter>().RouteAsync(request, CancellationToken.None);
    }

    /// <summary>Build via the REAL projection builder, retarget ONLY the planner llm.complete to the RecordReplay decorator's tag (the real/recorded model at the IStructuredLLMClient seam), then start the snapshot run via the REAL starter.</summary>
    private async Task<Guid> ProjectRetargetToRealModelAndStartAsync(RoutePlan route, Guid teamId, Guid userId)
    {
        var plannerRowId = await RecordReplayPlannerRowAsync(teamId);

        using var scope = _fixture.BeginScope();

        var context = new TaskBuildContext
        {
            Seed = PlanMapSynthPlannerRequest.Seed(teamId),
            Route = route,
            AgentProfile = new ResolvedAgentProfile { Harness = "codex-cli", RunnerKind = "local", AutonomyLevel = "Confined" },
        };

        var builder = scope.Resolve<ITaskProjectionRegistry>().Resolve(route.ProjectionKind);

        var definition = RetargetPlannerToRealModel(builder.Build(context), plannerRowId);

        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, teamId, userId, launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
    }

    /// <summary>Test-only adaptation: PIN the plan.author planner's model to the seeded RecordReplay pool row (the real/recorded model — the ONLY real model under test; pool-driven resolve lands on the record/replay client and pick.ModelId equals the drift mirror's DefaultModel), and retarget the SYNTH llm.complete to the deterministic plain-text synth fake. The synth MUST NOT hit the RecordReplay decorator — it has no cassette for the synth request and would throw; only the planner's plan authorship is under test here. The agent.code body + the graph SHAPE are left exactly as the production builder emitted them.</summary>
    private static WorkflowDefinition RetargetPlannerToRealModel(WorkflowDefinition definition, Guid plannerRowId) => definition with
    {
        Nodes = definition.Nodes.Select(n => RetargetNode(n, plannerRowId)).ToList(),
    };

    private static NodeDefinition RetargetNode(NodeDefinition node, Guid plannerRowId) => node.Id switch
    {
        "planner" => PinPlannerModel(node, plannerRowId),
        "synth" => RetargetProvider(node, DeterministicSynthLlmClient.ProviderTag),
        _ => node,
    };

    /// <summary>The planner is plan.author (pool-driven, no provider config) — pin its plannerModelId to the SEEDED RecordReplay pool row, so ResolveByRowIdAsync lands on the record/replay client and pick.ModelId equals the drift mirror's DefaultModel exactly.</summary>
    private static NodeDefinition PinPlannerModel(NodeDefinition node, Guid plannerRowId)
    {
        var config = node.Config.Deserialize<Dictionary<string, JsonElement>>() ?? new();
        config["plannerModelId"] = JsonSerializer.SerializeToElement(plannerRowId.ToString());

        return node with { Config = JsonSerializer.SerializeToElement(config) };
    }

    /// <summary>The RecordReplay provider's seeded credentialed-model row for this team — the row the planner pin targets.</summary>
    private async Task<Guid> RecordReplayPlannerRowAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.Credential.TeamId == teamId && m.Credential.Provider == RecordReplayStructuredLLMClient.ProviderTag)
            .Select(m => m.Id)
            .SingleAsync();
    }

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
