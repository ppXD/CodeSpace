using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
/// Engine ↔ agent-execution-layer round-trip for the <c>agent.code</c> node, against real Postgres + the
/// real engine + the real AgentRunService / completion notifier. The node SUSPENDS; the engine stages a
/// durable agent run + an <c>AgentRun</c> wait + dispatches the executor; the executor's completion
/// resumes the node — mapping the run's result onto the node's outputs (Succeeded) or failing the node
/// (anything else, composing with the Phase-2 error branch). Pins: the success round-trip + IO mapping;
/// a failed run → node fails; a failed run → the node's error branch; a duplicate completion notice does
/// not double-resume (no wasted re-run / re-spent tokens).
///
/// <para><b>Fidelity (Rule 12) — medium-mock:</b> every production class on the WIRING path is real
/// (engine suspend/resume, the AgentRunService state machine, the resume notifier, the agent.code node).
/// Only the harness EXECUTION is stood in for: <see cref="SimulateAgentExecutorAsync"/> drives the same
/// MarkRunning → Complete → Notify sequence the executor's <c>CompleteAndNotifyAsync</c> runs, minus the
/// sandboxed CLI (no codex binary in CI — the harness/runner contracts have their own suites). The
/// in-memory job client records the executor dispatch with <c>AutoExecute=false</c>, so the real harness
/// never spawns and the resume's re-dispatch is driven manually (mirroring SubworkflowFlowTests).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentNodeFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentNodeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Agent_node_suspends_stages_the_run_then_resumes_with_its_result()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentNodeDefinition(withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // record the executor dispatch; the real (binary-less) harness must not run

        try
        {
            // ── Pass 1: the node parks; the engine stages a linked agent run + an AgentRun wait + dispatches. ──
            await RunEngineAsync(runId);

            var agentRunId = await AssertStagedAndGetAgentRunIdAsync(runId, jobClient);

            // ── The executor finishes the run successfully (simulated — see the class doc). ──
            await SimulateAgentExecutorAsync(agentRunId, new AgentRunResult
            {
                Status = AgentRunStatus.Succeeded,
                ExitReason = "completed",
                Summary = "Fixed the failing billing tests.",
                ChangedFiles = new[] { "src/billing/Invoice.cs", "tests/Billing.Tests.cs" },
                ProducedBranch = "agent/fix-billing",
            });

            // ── Pass 2: the completion resumed the run; the node maps the result to outputs → terminal. ──
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(WorkflowRunStatus.Success, "the resumed node maps a Succeeded run to outputs → the run completes");

            (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId)).Status.ShouldBe(AgentRunStatus.Succeeded);

            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status
                .ShouldBe(WorkflowWaitStatuses.Resolved, "the AgentRun wait is resolved once the node resumes");

            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "agent")).Status
                .ShouldBe(NodeStatus.Success);

            // The agent's result flowed node → outputs → the terminal that forwarded them.
            var outputs = JsonDocument.Parse(run.OutputsJson).RootElement;
            outputs.GetProperty("summary").GetString().ShouldBe("Fixed the failing billing tests.");
            outputs.GetProperty("branch").GetString().ShouldBe("agent/fix-billing");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Failed_agent_run_fails_the_node_without_an_error_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentNodeDefinition(withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var agentRunId = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "patch did not apply" });

            await RunEngineAsync(runId);   // resume → node maps Failed → node fails → run fails

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Failure, "a failed agent run with no error branch fails the run");

            var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "agent");
            node.Status.ShouldBe(NodeStatus.Failure);
            node.Error.ShouldNotBeNull();
            node.Error!.ShouldContain("patch did not apply");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Failed_agent_run_takes_the_nodes_error_branch()
    {
        // The agent layer's failure composes with the Phase-2 error branch: a failed run makes the
        // agent.code node fail, which routes down its `error` edge to a handler instead of failing the run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentNodeDefinition(withErrorBranch: true));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var agentRunId = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "patch did not apply" });

            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the run's failure is handled by the agent.code node's error branch");
            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "agent")).Status
                .ShouldBe(NodeStatus.Failure);
            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "caught")).Status
                .ShouldBe(NodeStatus.Success, "the error handler ran");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Duplicate_completion_notice_does_not_double_resume()
    {
        // A re-claimed Hangfire job / a manual re-run can fire the completion notifier twice. The second
        // notice must be a no-op — never a second resume that re-runs the node + re-spends tokens. Two
        // gates enforce this: the notifier skips a non-pending wait, and ResumeCoreAsync's Suspended→Pending
        // CAS no-ops on an already-resumed run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentNodeDefinition(withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var agentRunId = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(agentRunId, SucceededResult());   // first completion → resume enqueued once
            await RunEngineAsync(runId);                                       // resume → Success

            // Fire the completion notice AGAIN, after the node already resumed + the wait resolved.
            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunCompletionNotifier>().NotifyCompletedAsync(agentRunId, CancellationToken.None);

            jobClient.Calls.Count(c => c.MethodName == "ExecuteRunAsync" && c.RunId == runId)
                .ShouldBe(1, "the duplicate completion notice must not enqueue a second resume");

            using var verify = _fixture.BeginScope();
            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the run stays Success — the duplicate notice changed nothing");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_crashed_agent_run_is_recovered_and_the_parent_workflow_resumes()
    {
        // The pod-death story end-to-end: a worker claims the run then vanishes. The reconciler abandons
        // the run AND resumes the parked workflow — so the workflow never hangs forever waiting on it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentNodeDefinition(withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var agentRunId = await GetAgentRunIdAsync(runId);

            // Worker claimed it (Running) then crashed — heartbeat goes stale past the liveness window.
            await MarkRunningAsync(agentRunId);
            await BackdateColumnAsync(agentRunId, "heartbeat_at", TimeSpan.FromMinutes(20));

            var summary = await ReconcileAsync();
            summary.MarkedAbandonedFromRunning.ShouldBeGreaterThanOrEqualTo(1, "the abandoned run is failed");
            summary.ResumedStalledParents.ShouldBeGreaterThanOrEqualTo(1, "and its parked workflow is resumed");

            await RunEngineAsync(runId);   // the resume re-runs the node, which maps the abandoned failure

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId)).Status.ShouldBe(AgentRunStatus.Failed);
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Failure, "the abandoned agent run fails the node → the run (no error branch)");
            (await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "agent")).Error!
                .ShouldContain("abandoned");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_stuck_queued_agent_run_is_re_dispatched_by_the_reconciler()
    {
        // The lost-dispatch crash window: the workflow committed Suspended but the executor was never
        // enqueued. The reconciler re-dispatches the stuck-Queued run so it isn't stranded forever.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentNodeDefinition(withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var agentRunId = await GetAgentRunIdAsync(runId);

            await BackdateColumnAsync(agentRunId, "created_date", TimeSpan.FromMinutes(20));   // looks stuck
            jobClient.Clear();   // forget the engine's original dispatch — we want to see the reconciler's

            var summary = await ReconcileAsync();
            summary.ReDispatchedQueued.ShouldBeGreaterThanOrEqualTo(1);

            jobClient.Calls.Count(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync) && c.RunId == agentRunId)
                .ShouldBe(1, "the reconciler re-dispatches the stuck queued run exactly once");

            using var verify = _fixture.BeginScope();
            (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId)).Status
                .ShouldBe(AgentRunStatus.Queued, "re-dispatch doesn't change status — the executor claims it when it runs");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Agent_node_with_a_persona_stages_the_resolved_merged_task()
    {
        // The persona-reference happy path end-to-end: the node carries only an AgentDefinitionId + a small
        // goal; the engine resolves the persona at staging (its system prompt prepends the goal, its model
        // fills in) and FREEZES the merged task into the run's TaskJson — self-describing + deterministic.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var personaId = await SeedPersonaAsync(teamId, userId, "You are a careful billing engineer.", "gpt-5.4");
        var workflowId = await CreateWorkflowAsync(teamId, userId, PersonaAgentNodeDefinition(personaId.ToString()));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var agentRun = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId);
            var task = JsonSerializer.Deserialize<AgentTask>(agentRun.TaskJson, AgentJson.Options)!;

            task.AgentDefinitionId.ShouldBe(personaId, "the persona reference is frozen into TaskJson as run provenance");
            task.Goal.ShouldBe("You are a careful billing engineer.\n\nFix the billing bug.",
                customMessage: "the engine resolves the persona at staging: its system prompt prepends the node goal, persisted as the final task");
            task.Model.ShouldBe("gpt-5.4", "the node set no model inline → the persona's model fills in");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task Agent_node_referencing_a_missing_persona_fails_the_node_with_no_orphan_run()
    {
        // A node pointing at a persona that doesn't exist for the team is a clean node failure at staging —
        // the resolver throws, the engine maps it to a node failure, and NO agent run is ever created.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PersonaAgentNodeDefinition(Guid.NewGuid().ToString()));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(WorkflowRunStatus.Failure, "an unresolvable persona fails the node → the run (no error branch)");
            run.Error!.ShouldContain("not found", Case.Insensitive,
                customMessage: "the resolution failure surfaces as the run error — the engine wraps the typed resolver exception into a clean node failure");
            (await db.AgentRun.AsNoTracking().AnyAsync(r => r.WorkflowRunId == runId))
                .ShouldBeFalse("resolution fails BEFORE CreateAsync — no orphan agent run is persisted");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedPersonaAsync(Guid teamId, Guid userId, string systemPrompt, string? model)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = "billing-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Billing Engineer",
            SystemPrompt = systemPrompt,
            Model = model,
            Origin = AgentDefinitionOrigin.Authored,
            CreatedDate = now,
            CreatedBy = userId,
            LastModifiedDate = now,
            LastModifiedBy = userId,
        };
        db.AgentDefinition.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private async Task<AgentRunReconcileSummary> ReconcileAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);
    }

    private async Task MarkRunningAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunService>().MarkRunningAsync(agentRunId, CancellationToken.None);
    }

    /// <summary>Backdate a timestamp column on an agent run so the default liveness window treats it as stale (column is a fixed literal; the value is parameterised).</summary>
    private async Task BackdateColumnAsync(Guid agentRunId, string column, TimeSpan ago)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlRawAsync($"UPDATE agent_run SET {column} = {{0}} WHERE id = {{1}}", DateTimeOffset.UtcNow - ago, agentRunId);
    }

    private async Task<Guid> AssertStagedAndGetAgentRunIdAsync(Guid runId, InMemoryBackgroundJobClient jobClient)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "the node parks while the agent run executes out-of-band");

        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId);
        agentRun.Status.ShouldBe(AgentRunStatus.Queued, "the staged run is queued — dispatched but not yet claimed");
        agentRun.NodeId.ShouldBe("agent", "the run links back to the node that spawned it");
        agentRun.Harness.ShouldBe("codex-cli");

        // The whole task envelope round-tripped node → engine → AgentRunService.
        var task = JsonSerializer.Deserialize<AgentTask>(agentRun.TaskJson, AgentJson.Options)!;
        task.Goal.ShouldBe("Fix the failing billing tests");
        task.Model.ShouldBe("gpt-5.3-codex");
        task.RunnerKind.ShouldBe("local");
        task.TimeoutSeconds.ShouldBe(900);
        task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly);

        var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
        wait.WaitKind.ShouldBe(WorkflowWaitKinds.AgentRun);
        wait.Status.ShouldBe(WorkflowWaitStatuses.Pending);
        wait.Token.ShouldBe(agentRun.Id.ToString(), "the wait's token is the agent-run id — the resume correlation key");

        var dispatches = jobClient.Calls
            .Where(c => c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync))
            .ToList();
        dispatches.Count.ShouldBe(1, "the agent run is dispatched to the executor exactly once on suspend");
        dispatches[0].RunId.ShouldBe(agentRun.Id, "the dispatch carries the staged agent-run id");

        return agentRun.Id;
    }

    private async Task<Guid> GetAgentRunIdAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId)).Id;
    }

    // Drive the executor's terminal sequence (MarkRunning → Complete → Notify) without the sandboxed CLI.
    // One scope = one DbContext, mirroring the executor's single-scope completion.
    private async Task SimulateAgentExecutorAsync(Guid agentRunId, AgentRunResult result)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, result, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private static AgentRunResult SucceededResult() => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        Summary = "Done.",
        ProducedBranch = "agent/done",
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "agent-" + Guid.NewGuid().ToString("N")[..6],
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

    // manual → agent.code → terminal(forwards summary + branch). Optionally agent =(error)=> caught.
    private static WorkflowDefinition AgentNodeDefinition(bool withErrorBranch)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.code",
                    Config = WorkflowsTestSeed.Json("""{"goal":"Fix the failing billing tests","harness":"codex-cli","model":"gpt-5.3-codex","runnerKind":"local","timeoutSeconds":900,"readOnly":true}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"summary":"{{nodes.agent.outputs.summary}}","branch":"{{nodes.agent.outputs.branch}}"}""") },
        };

        if (withErrorBranch)
            nodes.Add(new() { Id = "caught", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() });

        var edges = new List<EdgeDefinition> { new() { From = "start", To = "agent" }, new() { From = "agent", To = "end" } };

        if (withErrorBranch)
            edges.Add(new() { From = "agent", To = "caught", SourceHandle = WorkflowHandles.Error });

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }

    // manual → agent.code (references a persona by id; no inline model; a small task goal) → terminal.
    private static WorkflowDefinition PersonaAgentNodeDefinition(string agentDefinitionId)
    {
        var nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "agent", TypeKey = "agent.code",
                    Config = WorkflowsTestSeed.Json($$"""{"harness":"codex-cli","agentDefinitionId":"{{agentDefinitionId}}","goal":"Fix the billing bug."}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{"summary":"{{nodes.agent.outputs.summary}}"}""") },
        };

        var edges = new List<EdgeDefinition> { new() { From = "start", To = "agent" }, new() { From = "agent", To = "end" } };

        return new WorkflowDefinition { SchemaVersion = 1, Nodes = nodes, Edges = edges };
    }
}
