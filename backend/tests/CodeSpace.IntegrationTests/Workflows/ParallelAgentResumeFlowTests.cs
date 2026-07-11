using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
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
/// Regression for the parallel <c>agent.run</c> resume-corruption bug, against real Postgres + the real
/// engine + the real AgentRunService / completion notifier. Two unconnected agent.run nodes run in ONE
/// parallel wave; each MUST resume with its OWN agent's result. Pins all three fixed behaviours:
/// <list type="bullet">
///   <item><b>dispatch-all</b> — both staged agent runs are dispatched on suspend (not just the first);</item>
///   <item><b>dispatch-on-last</b> — the run stays Suspended until the LAST agent completes (a sibling
///         completion alone does not advance it);</item>
///   <item><b>no cross-contamination</b> — each node's outputs are its own agent's result, never the first
///         completer's (the corruption); a duplicate completion notice is a no-op.</item>
/// </list>
/// Same fidelity model as <see cref="AgentNodeFlowTests"/> (medium-mock): every wiring class is real; only
/// the sandboxed CLI is stood in for by <see cref="SimulateAgentCompletionAsync"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ParallelAgentResumeFlowTests
{
    private readonly PostgresFixture _fixture;

    public ParallelAgentResumeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Two_parallel_agents_dispatch_both_resume_on_last_and_keep_their_own_results()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, TwoParallelAgentsDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // record dispatches; the binary-less harness must not run

        try
        {
            // ── Pass 1: both agent.run nodes park in one wave → two AgentRun waits, run Suspended. ──
            await RunEngineAsync(runId);

            var (agentA, agentB) = await GetTwoAgentRunIdsAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
                (await db.WorkflowRunWait.AsNoTracking().CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending))
                    .ShouldBe(2, "a parallel wave parks BOTH agent.run nodes on their own AgentRun wait");
            }

            // dispatch-all: BOTH staged agent runs were enqueued on suspend (the bug enqueued only the first).
            var dispatched = jobClient.Calls
                .Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync))
                .Select(c => c.RunId)
                .ToList();
            dispatched.Count.ShouldBe(2, "both agent runs are dispatched to the executor on suspend");
            dispatched.ShouldContain(agentA);
            dispatched.ShouldContain(agentB);

            // ── Agent A completes FIRST, with ITS OWN distinct result. ──
            await SimulateAgentCompletionAsync(agentA, "RESULT-A", "agent/branch-a");

            // dispatch-on-last: A's completion resolves ONLY A's wait; the run stays Suspended for B.
            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();
                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "one of two parallel agents finishing does NOT advance the run");

                var waitA = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == agentA.ToString());
                var waitB = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == agentB.ToString());
                waitA.Status.ShouldBe(WorkflowWaitStatuses.Resolved, "A's own wait is resolved");
                waitB.Status.ShouldBe(WorkflowWaitStatuses.Pending, "B's wait is UNTOUCHED — the corruption resolved it too");
                waitA.PayloadJson!.ShouldContain("RESULT-A");
            }

            // ── Agent B completes with ITS OWN distinct result → the last wait → the run advances. ──
            await SimulateAgentCompletionAsync(agentB, "RESULT-B", "agent/branch-b");
            await RunEngineAsync(runId);   // resume pass — one re-walk consumes both resolved waits

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

                // The crux: each node resumed with ITS OWN agent's result — never the first completer's.
                var nodeA = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "agentA");
                var nodeB = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "agentB");
                nodeA.Status.ShouldBe(NodeStatus.Success);
                nodeB.Status.ShouldBe(NodeStatus.Success);
                nodeA.OutputsJson.ShouldNotBeNull();
                nodeB.OutputsJson.ShouldNotBeNull();
                JsonDocument.Parse(nodeA.OutputsJson!).RootElement.GetProperty("summary").GetString()
                    .ShouldBe("RESULT-A", "agentA resumes with agentA's result");
                JsonDocument.Parse(nodeB.OutputsJson!).RootElement.GetProperty("summary").GetString()
                    .ShouldBe("RESULT-B", "agentB resumes with agentB's result — NOT the first completer's (the bug)");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_duplicate_completion_on_one_parallel_agent_is_a_noop()
    {
        // Re-notifying agent A after it already resolved its wait must not re-stamp it, double-dispatch, or
        // disturb the still-pending sibling B.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, TwoParallelAgentsDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var (agentA, agentB) = await GetTwoAgentRunIdsAsync(runId);

            await SimulateAgentCompletionAsync(agentA, "RESULT-A", "agent/branch-a");

            // Fire A's completion notice AGAIN (a re-claimed Hangfire job / manual re-run).
            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunCompletionNotifier>().NotifyCompletedAsync(agentA, CancellationToken.None);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the duplicate notice changed nothing — B is still pending");
            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.Token == agentB.ToString())).Status
                .ShouldBe(WorkflowWaitStatuses.Pending, "the sibling wait is untouched by A's duplicate notice");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid AgentA, Guid AgentB)> GetTwoAgentRunIdsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var a = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId && r.NodeId == "agentA");
        var b = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId && r.NodeId == "agentB");
        return (a.Id, b.Id);
    }

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI.
    private async Task SimulateAgentCompletionAsync(Guid agentRunId, string summary, string branch)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ProducedBranch = branch,
        }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "parallel-agents-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
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

    // manual → { agentA, agentB } in parallel → a join terminal forwarding both summaries.
    private static WorkflowDefinition TwoParallelAgentsDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agentA", TypeKey = "agent.run",
                    Config = WorkflowsTestSeed.Json("""{"goal":"Task A","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","readOnly":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agentB", TypeKey = "agent.run",
                    Config = WorkflowsTestSeed.Json("""{"goal":"Task B","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","readOnly":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"a":"{{nodes.agentA.outputs.summary}}","b":"{{nodes.agentB.outputs.summary}}"}""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "agentA" },
            new() { From = "start", To = "agentB" },
            new() { From = "agentA", To = "end" },
            new() { From = "agentB", To = "end" },
        },
    };
}
