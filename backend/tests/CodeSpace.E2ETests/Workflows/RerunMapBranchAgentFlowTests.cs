using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// D7-5 agent-bodied map-branch rerun — the crown jewel for the "agent-branch(execute-again)" v1 goal. Re-run ONE
/// branch of a top-level flow.map whose body is a REAL <c>agent.code</c> node, reusing the N-1 sibling branches,
/// driving the actual durable agent suspend/resume on the fork.
///
/// <para><b>Tier: high-fidelity (the same harness the supervisor + plan-map-synth E2Es use).</b> The real
/// <see cref="IWorkflowService.RerunMapBranchAsync"/> forks the run; the real engine re-enters the map, replays the
/// seeded siblings from the ledger (NO agent re-stage), and re-runs ONLY the target branch — whose <c>agent.code</c>
/// node parks an AgentRun under <c>map#i</c>, dispatches the REAL <see cref="Core.Services.Agents.IAgentRunExecutor"/>
/// → real <c>LocalProcessRunner</c> → the <see cref="SubtaskAwareFakeCli"/> process → real ParseEvent/BuildResult →
/// natural resume → the map barrier completes → the downstream synthesizer re-runs over the new aggregate. Only the
/// CLI's intelligence is faked, at the binary (POSIX-only, Rule 12.1). This is the agent path D7-5 lifted the
/// pure-body restriction to admit (<see cref="Rerun.RerunBranchBodyPolicy"/> opts in agent.code alone); the
/// per-element side-effecting / refusal / drift-detector coverage lives in <c>RerunMapBranchFlowTests</c>.</para>
///
/// <para>The discriminators: the fork re-stages EXACTLY ONE fresh AgentRun (for the target branch, keyed
/// <c>(forkRunId, map#i)</c> with the per-branch goal), the N-1 siblings re-stage ZERO agents + carry zero
/// node.started on the fork, and the synthesizer re-runs.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class RerunMapBranchAgentFlowTests
{
    private readonly PostgresFixture _fixture;

    public RerunMapBranchAgentFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string FourGoals = """{ "things": ["e0", "e1", "e2", "e3"] }""";

    [Fact]
    public async Task Rerun_one_agent_bodied_map_branch_restages_only_the_target_agent_reuses_siblings_and_re_synthesizes()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns (Rule 12.1)

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // an agent.code suspend dispatches the REAL executor + runner + fake CLI, then resumes

        // ── Original: map fans out 4 real agent.code branches, each runs the fake CLI to Succeeded; map Success. ──
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentMapDef());
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: FourGoals);
        await RunEngineAsync(originalRunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        (await AgentRunCountAsync(originalRunId)).ShouldBe(4, "the original fanned out one real AgentRun per branch");

        // ── Rerun branch 2. The fork re-stages ONLY branch 2's agent; siblings 0/1/3 are replayed terminal-only. ──
        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 2, teamId, userId);
        await RunEngineAsync(rerunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        // EXACTLY ONE fresh AgentRun on the fork — the target branch — keyed (forkRunId, agent, map#2) with its goal.
        var forkAgents = await LoadAgentRunsAsync(rerunId);
        forkAgents.Count.ShouldBe(1, "only the re-run target branch re-stages an agent; the 3 siblings are replayed, NOT re-staged");
        forkAgents[0].NodeId.ShouldBe("agent");
        forkAgents[0].IterationKey.ShouldBe("map#2", "the re-staged agent is keyed to the target branch");
        forkAgents[0].Status.ShouldBe(AgentRunStatus.Succeeded, "the re-run target agent executed to completion via the real executor + fake CLI");
        AgentGoalOf(forkAgents[0]).ShouldBe("Work on e2", "the target branch re-resolved its own {{item}} goal");

        // Siblings carry zero node.started on the fork (replayed from the seeded terminal cells); the target ran
        // EXACTLY twice — one park-walk + one resume-walk (the agent suspend/resume shape). More would be a
        // #306-class over-execution re-walk that reuses the AgentRun row and slips past forkAgents.Count==1.
        (await BranchStartedCountAsync(rerunId, "agent", "map#0")).ShouldBe(0, "sibling 0 was replayed, not re-run");
        (await BranchStartedCountAsync(rerunId, "agent", "map#1")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "agent", "map#3")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "agent", "map#2")).ShouldBe(2, "the target branch re-ran its agent exactly twice: park-walk + resume-walk");

        // The map re-aggregated all 4 branches in ELEMENT ORDER: results[i] holds branch e{i}'s real fake-CLI
        // summary, and the re-staged target (index 2) carries its freshly-derived summary — a scrambled
        // re-aggregation on the agent path would fail this.
        var results = await LoadMapResultsAsync(rerunId, "map");
        results.GetArrayLength().ShouldBe(4, "the re-aggregated map carries all 4 branches (3 replayed + 1 fresh)");
        for (var i = 0; i < 4; i++)
            results[i].GetProperty("summary").GetString().ShouldBe(SubtaskAwareFakeCli.ExpectedSummaryFor($"Work on e{i}"), $"results[{i}] must hold branch e{i}'s summary in element order");
        (await NodeStartedCountAsync(rerunId, "synth")).ShouldBe(1, "the downstream synthesizer re-ran over the re-aggregated results");
    }

    [Fact]
    public async Task Rerun_composite_agent_plus_side_effecting_branch_body_restages_agent_once_then_gates_the_side_effect()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        // Body = agent.code → side-effecting node. On a rerun the branch parks the agent's AgentRun wait first;
        // after the agent completes it re-walks and the side-effecting node parks the D7-3 Approval gate. The two
        // waits of DIFFERENT kinds coexist under the same branch key (NodeId-keyed payloads never collide), the
        // agent re-derives from its payload (NOT re-staged), and the side effect fires only after approval.
        var probeKey = "mapbranch-composite-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 2; i++) MutatingProbeNode.Reset($"{probeKey}-e{i}");

        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentThenSideEffectMapDef(probeKey));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["e0", "e1"] }""");
        await RunEngineAsync(originalRunId);
        await jobClient.WaitForPendingAsync();
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        (await AgentRunCountAsync(originalRunId)).ShouldBe(2, "original: one agent per branch");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e0").ShouldBe(1, "original: branch 0 side effect fired once");

        // Rerun branch 0: the agent re-stages + runs (AutoExecute), then the branch re-walks to the side-effecting
        // node which parks the gate — the run ends Suspended on that gate, the effect NOT yet re-fired.
        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId);
        await RunEngineAsync(rerunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);
        (await LoadAgentRunsAsync(rerunId)).Count.ShouldBe(1, "exactly ONE agent re-staged on the fork (the target) — the agent re-derived on resume, not re-staged again");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(1, "the side-effecting node parked its D7-3 gate after the agent resumed");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e0").ShouldBe(1, "the side effect has NOT re-fired before approval");

        // Approve → the side effect fires exactly once, the branch completes, the fork succeeds.
        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e0").ShouldBe(2, "the approved side effect fired exactly once more");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e1").ShouldBe(1, "the sibling branch was replayed — its side effect never re-fired");
        (await LoadAgentRunsAsync(rerunId)).Count.ShouldBe(1, "still exactly one agent on the fork after approval (no spurious re-stage)");
    }

    // ── Definition: start → map(items={{trigger.things}}; body: ms → agent[goal="Work on {{item}}"]) → synth → end. ──
    private static WorkflowDefinition AgentMapDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.code", ParentId = "map",
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Work on {{item}}", "harness": "codex-cli" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "synth", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "agg": "{{nodes.map.outputs.results}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "synth", To = "end" },
            new() { From = "ms", To = "agent" },
        },
    };

    // ── Composite body: start → map(body: ms → agent → se[side-effecting]) → synth → end. ──
    private static WorkflowDefinition AgentThenSideEffectMapDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.code", ParentId = "map",
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Work on {{item}}", "harness": "codex-cli" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "se", TypeKey = MutatingProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.Json($$"""{ "key": "{{probeKey}}-{{"{{"}}item{{"}}"}}" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "synth", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "agg": "{{nodes.map.outputs.results}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "synth", To = "end" },
            new() { From = "ms", To = "agent" },
            new() { From = "agent", To = "se" },
        },
    };

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "mapbranch-agent-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> RerunMapBranchAsync(Guid originalRunId, string mapNodeId, int branchIndex, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunMapBranchAsync(originalRunId, mapNodeId, branchIndex, teamId, userId, operationId: null, CancellationToken.None);
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

    private async Task AssertRunStatusAsync(Guid runId, WorkflowRunStatus expected)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(expected, $"run {runId}; error={run.Error}");
    }

    private async Task<int> AgentRunCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId);
    }

    private async Task<bool> ApproveRerunGateAsync(Guid runId, Guid teamId, Guid userId, bool approved)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = approved, Comment = approved ? "go" : "skip" });
    }

    private async Task<int> PendingApprovalWaitCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.Approval);
    }

    private async Task<List<Core.Persistence.Entities.AgentRun>> LoadAgentRunsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
    }

    private static string AgentGoalOf(Core.Persistence.Entities.AgentRun run) =>
        JsonSerializer.Deserialize<Messages.Agents.AgentTask>(run.TaskJson, Core.Services.Agents.AgentJson.Options)!.Goal;

    private async Task<JsonElement> LoadMapResultsAsync(Guid runId, string mapNodeId)
    {
        using var scope = _fixture.BeginScope();
        var cell = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking()
            .SingleAsync(n => n.RunId == runId && n.NodeId == mapNodeId && n.IterationKey == "");
        return JsonDocument.Parse(cell.OutputsJson).RootElement.GetProperty("results").Clone();
    }

    private async Task<int> NodeStartedCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }

    private async Task<int> BranchStartedCountAsync(Guid runId, string nodeId, string branchKey)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == branchKey && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }
}
