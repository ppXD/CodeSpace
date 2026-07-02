using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows;
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
/// D1 (retry-resume) — the supervisor's "continue" end-to-end through the REAL turn loop + <see cref="SupervisorTurnService"/>
/// + <see cref="RealSupervisorActionExecutor"/> + real <see cref="AgentRunService"/> over real Postgres (the scripted
/// decider stands in for the LLM; agent completion is simulated like <see cref="SupervisorSpawnFlowTests"/> — no CLI).
///
/// <para>The supervisor plans 2 subtasks → spawns both → subtask <c>sb</c>'s attempt CAPTURES a session + transcript →
/// the barrier resumes to turn 2 = RETRY <c>sb</c>. The proof: the re-staged retry agent's task carries the RESUME HINT
/// (<c>ResumeFromSessionId</c> + <c>RestoredTranscript</c>) resolved from that failed attempt — so the retried agent
/// CONTINUES the conversation instead of cold-restarting. The executor→harness→restore half is proven by A1's
/// RerunFromNodeAgentFlowTests + the RealModelSessionResume gate (the retry agent runs the identical path).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorRetryResumeFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;

    public SupervisorRetryResumeFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().Mode = SupervisorScriptMode.PlanSpawnRetryMergeStop;   // plan → spawn[sa,sb] → retry(sb) → merge → stop
    }

    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();   // restore the default for sibling tests
    }

    [Fact]
    public async Task A_supervisor_retry_resumes_the_failed_attempts_conversation()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // binary-less; we simulate completion + inspect the re-staged retry task BEFORE it runs

        try
        {
            // Turn 0: plan → self-advance → Turn 1: spawn[sa, sb].
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            Guid agentSa, agentSb;
            using (var verify = _fixture.BeginScope())
            {
                var waits = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                    .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending)
                    .OrderBy(w => w.IterationKey).ToListAsync();

                waits.Count.ShouldBe(2, "spawn[sa,sb] staged two real agent runs");
                agentSa = Guid.Parse(waits[0].Token);   // sup#turn1#0 = SubtaskA
                agentSb = Guid.Parse(waits[1].Token);   // sup#turn1#1 = SubtaskB
            }

            // sb's attempt CAPTURES a session + transcript (as the Claude harness would); both complete → barrier → turn 2.
            await SimulateAgentCompletionAsync(agentSa, "sess-sa", "alpha convo\n");
            await SimulateAgentCompletionAsync(agentSb, "sess-sb-attempt1", "the beta conversation to resume\n");
            await RunEngineAsync(runId);   // turn 2: retry(sb)

            using (var verify = _fixture.BeginScope())
            {
                // The retry re-staged a FRESH agent for sb at the turn-2 cell, carrying sb's failed attempt's session.
                var retryAgent = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
                    .SingleAsync(r => r.WorkflowRunId == runId && r.IterationKey == "sup#turn2");

                var task = JsonSerializer.Deserialize<AgentTask>(retryAgent.TaskJson, AgentJson.Options)!;

                task.SubtaskId.ShouldBe("sb", "the retry agent is stamped with its subtask id (the retry-resume linking key)");
                task.ResumeFromSessionId.ShouldBe("sess-sb-attempt1", "the retry RESUMES sb's failed attempt's conversation — not a cold restart");
                task.RestoredTranscript.ShouldBe("the beta conversation to resume\n", "and restores THAT attempt's transcript");
                task.Goal.ShouldContain("do beta retry", customMessage: "while still carrying the retry's revised instruction");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers (self-contained per the supervisor-flow-test convention) ─────────────────────

    private async Task SimulateAgentCompletionAsync(Guid agentRunId, string sessionId, string transcript)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", SessionId = sessionId, SessionTranscript = transcript }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private async Task ResolveSelfAdvanceAsync(Guid runId)
    {
        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            waitId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)).Id;
        }

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
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

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-retry-resume-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private static WorkflowDefinition SupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
