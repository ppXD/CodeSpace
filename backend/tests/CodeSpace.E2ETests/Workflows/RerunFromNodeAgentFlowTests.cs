using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// P2.2 — agent.code as a from-node rerun ROOT (not just a map-branch body). Re-run a TOP-LEVEL flow from a real
/// <c>agent.code</c> node: the fork mints a new run id, replays the kept upstream agent from the ledger (NO re-stage),
/// and re-stages EXACTLY ONE fresh AgentRun for the from-node target on the forked run id — driving the actual durable
/// agent suspend/resume to completion.
///
/// <para><b>Tier: high-fidelity</b> — the same harness as <see cref="RerunMapBranchAgentFlowTests"/>: the real
/// <see cref="IWorkflowService.RerunFromNodeAsync"/> forks, the real engine re-walks the ReRun closure, the target's
/// <c>agent.code</c> node parks an AgentRun, dispatches the REAL <see cref="Core.Services.Agents.IAgentRunExecutor"/>
/// → real <c>LocalProcessRunner</c> → the <see cref="SubtaskAwareFakeCli"/> process → real ParseEvent/BuildResult →
/// natural resume → the run completes. Only the CLI's intelligence is faked, at the binary (POSIX-only, Rule 12.1).</para>
///
/// <para>This proves the one-line P2.2 disposition flip (<c>RerunDispositions</c> admits <c>ReStageExternalRun</c> as a
/// from-node root) needs NO engine change: a from-node fork's new run id makes the re-staged AgentRun unique by
/// construction, and the stateless agent.code node re-walks through the SAME generic stage chain it uses on a first
/// run. The discriminators mirror the map-branch crown jewel: the fork re-stages EXACTLY ONE fresh AgentRun (distinct
/// Id, the fork's WorkflowRunId, the target's own goal), the kept upstream agent carries zero node.started + is NOT
/// re-staged, and the from-node target ran exactly twice (park-walk + resume-walk).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class RerunFromNodeAgentFlowTests
{
    private readonly PostgresFixture _fixture;

    public RerunFromNodeAgentFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Rerun_from_an_agent_code_node_restages_only_that_agent_reuses_the_upstream_agent_and_succeeds()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns (Rule 12.1)

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // an agent.code suspend dispatches the REAL executor + runner + fake CLI, then resumes

        // ── Original: start → agent(a:"Work on alpha") → agent(b:"Work on beta") → end. Both run to Succeeded. ──
        var workflowId = await CreateWorkflowAsync(teamId, userId, TwoAgentChainDef());
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        var originalAgents = await LoadAgentRunsAsync(originalRunId);
        originalAgents.Count.ShouldBe(2, "the original chain staged one AgentRun per agent node");
        var originalB = originalAgents.Single(r => r.NodeId == "b");
        originalB.IterationKey.ShouldBe("", "a top-level agent.code is keyed TopLevel");

        // ── Rerun FROM node "b". The fork keeps "a" (upstream → replayed, NOT re-staged) and re-stages ONLY "b". ──
        var rerunId = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);
        await RunEngineAsync(rerunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        // EXACTLY ONE fresh AgentRun on the fork — the from-node target "b" — keyed (forkRunId, b, TopLevel), distinct
        // Id from the original's "b", carrying its own re-resolved goal. "a" is replayed, never re-staged.
        var forkAgents = await LoadAgentRunsAsync(rerunId);
        forkAgents.Count.ShouldBe(1, "only the from-node target re-stages an agent; the kept upstream agent is replayed, NOT re-staged");
        var forkB = forkAgents[0];
        forkB.NodeId.ShouldBe("b");
        forkB.IterationKey.ShouldBe("", "the re-staged from-node agent is keyed TopLevel");
        forkB.WorkflowRunId.ShouldBe(rerunId, "the re-staged AgentRun belongs to the FORK's run id — the source of its uniqueness");
        forkB.Id.ShouldNotBe(originalB.Id, "a from-node rerun mints a FRESH AgentRun, it never reuses the original's row");
        forkB.Status.ShouldBe(AgentRunStatus.Succeeded, "the re-run target agent executed to completion via the real executor + fake CLI");
        AgentGoalOf(forkB).ShouldBe("Work on beta", "the from-node target re-resolved its own goal");

        // The kept upstream agent "a" carries zero node.started on the fork (replayed from its seeded terminal cell);
        // the target "b" ran EXACTLY twice — one park-walk + one resume-walk (the agent suspend/resume shape).
        (await NodeStartedCountAsync(rerunId, "a")).ShouldBe(0, "the kept upstream agent was replayed, not re-run");
        (await NodeStartedCountAsync(rerunId, "b")).ShouldBe(2, "the from-node target re-ran its agent exactly twice: park-walk + resume-walk");

        // The original run is untouched — its AgentRuns still belong to the original run id (no cross-run mutation).
        (await AgentRunCountAsync(originalRunId)).ShouldBe(2, "the original run's AgentRuns are unchanged by the fork");
    }

    // ── Definition: start → agent(a) → agent(b) → end. Two top-level real agent.code nodes with distinct goals. ──
    private static WorkflowDefinition TwoAgentChainDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = "agent.code",
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Work on alpha", "harness": "codex-cli" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "b", TypeKey = "agent.code",
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Work on beta", "harness": "codex-cli" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "b" },
            new() { From = "b", To = "end" },
        },
    };

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "fromnode-agent-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> RerunFromNodeAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunFromNodeAsync(originalRunId, fromNodeId, teamId, userId, CancellationToken.None);
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

    private async Task<List<Core.Persistence.Entities.AgentRun>> LoadAgentRunsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
    }

    private static string AgentGoalOf(Core.Persistence.Entities.AgentRun run) =>
        JsonSerializer.Deserialize<Messages.Agents.AgentTask>(run.TaskJson, Core.Services.Agents.AgentJson.Options)!.Goal;

    private async Task<int> NodeStartedCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }
}
