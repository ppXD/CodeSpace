using System.Text.Json;
using Autofac;
using CodeSpace.Core.Constants;
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
    public async Task The_long_agent_run_executor_is_enqueued_on_the_dedicated_agent_queue()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, AgentNodeDefinition(withErrorBranch: false));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;   // record the executor dispatch; don't run the (binary-less) harness

        try
        {
            // The agent.code node parks and enqueues its LONG-running executor job. It MUST land on the dedicated
            // agent queue, not the control-plane "default" queue — that isolation is the whole point of the split:
            // enough concurrent agent runs would otherwise occupy every worker and starve the reconcilers / expiry.
            await RunEngineAsync(runId);

            var agentEnqueue = jobClient.Calls.Single(c =>
                c.ServiceType == typeof(IAgentRunExecutor) && c.MethodName == nameof(IAgentRunExecutor.ExecuteAsync));

            agentEnqueue.Queue.ShouldBe(HangfireConstants.AgentQueue,
                "the long agent.code executor job rides the dedicated agents queue so it can't starve the control plane");
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

            // Worker claimed it (Running) then crashed — it stopped renewing its lease, which lapses past the
            // liveness window (the reconciler gates on the lease, not heartbeat-silence).
            await MarkRunningAsync(agentRunId);
            await BackdateColumnAsync(agentRunId, "lease_expires_at", TimeSpan.FromMinutes(20));

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
    public async Task A_non_terminal_run_never_resumes_the_parent_with_a_running_status()
    {
        // Guards the "Agent run did not succeed: Running" bug: if the completion notifier fires while the run
        // is still in flight (a completion racing the reconciler, an inconsistent mid-transition row), it must
        // NOT resume the parent — handing the node a non-terminal status reads as a bogus failure. The wait
        // stays Pending; the reconciler resumes the parent only once a real TERMINAL status lands.
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

            await MarkRunningAsync(agentRunId);   // Running — NOT terminal

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunCompletionNotifier>().NotifyCompletedAsync(agentRunId, CancellationToken.None);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status
                .ShouldBe(WorkflowWaitStatuses.Pending, "a non-terminal run must not resolve the AgentRun wait");
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the parent stays parked — never resumed (and failed) with a non-terminal status");
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
            task.Goal.ShouldBe("Fix the billing bug.", "B1: the goal stays the clean node task — no persona baked in");
            task.SystemPrompt.ShouldBe("You are a careful billing engineer.", "the engine resolves the persona at staging into its own SystemPrompt channel, persisted on the final task");
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

    [Fact]
    public async Task The_run_detail_links_the_agent_node_to_its_run_and_streams_its_events_team_scoped()
    {
        // Phase-1 observability foundation: the run detail carries the agent run id for the live timeline,
        // and the run's status + events are readable team-scoped (a foreign team sees neither).
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

            // Stream a live step onto the run, as the executor would.
            using (var scope = _fixture.BeginScope())
            {
                var runs = scope.Resolve<IAgentRunService>();
                await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
                await runs.AppendEventAsync(agentRunId, new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "Analyzing the repo…" }, CancellationToken.None);
            }

            using var verify = _fixture.BeginScope();

            var detail = await verify.Resolve<CodeSpace.Core.Services.Workflows.IWorkflowService>().GetRunAsync(runId, teamId, CancellationToken.None);
            detail!.Nodes.Single(n => n.NodeId == "agent").AgentRunId
                .ShouldBe(agentRunId.ToString(), "the agent.code node carries its agent run id for live streaming");

            var svc = verify.Resolve<IAgentRunService>();

            var summary = (await svc.GetSummaryForTeamAsync(agentRunId, teamId, CancellationToken.None))!;
            summary.Status.ShouldBe(AgentRunStatus.Running);
            summary.Goal.ShouldNotBeNullOrWhiteSpace("the agent's goal/instruction is projected from the durable TaskJson so the terminal can show WHAT it was told to do");
            (await svc.GetEventsAsync(agentRunId, teamId, 0, CancellationToken.None)).ShouldContain(e => e.Text == "Analyzing the repo…");

            // A foreign team leaks neither status nor events.
            (await svc.GetSummaryForTeamAsync(agentRunId, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
            (await svc.GetEventsAsync(agentRunId, Guid.NewGuid(), 0, CancellationToken.None)).ShouldBeEmpty();
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

    // ── P0.3: transient-failure respawn — the retry policy re-stages a FRESH agent across suspend cycles ──

    [Fact]
    public async Task A_transient_agent_failure_respawns_a_fresh_agent_and_the_retry_succeeds()
    {
        // Retry {2 attempts, 0s backoff}: agent #1 dies transiently → the resume consumes attempt 1 and re-runs the
        // node FRESH, staging a SECOND agent run under a new wait; its success completes the run. One transient
        // death no longer kills the whole task.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, RetryingAgentNodeDefinition(maxAttempts: 2));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var firstAgent = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(firstAgent, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "rate limited" });

            await RunEngineAsync(runId);   // resume → attempt 1 fails → the retry re-stages a FRESH agent + parks again

            Guid secondAgent;
            using (var mid = _fixture.BeginScope())
            {
                var db = mid.Resolve<CodeSpaceDbContext>();

                var agents = await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).OrderBy(r => r.CreatedDate).ToListAsync();
                agents.Count.ShouldBe(2, "the retry staged a FRESH agent run — never re-read the settled failure");
                secondAgent = agents.Single(a => a.Id != firstAgent).Id;

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Suspended, "the respawned agent parks the run again — not a failure");

                (await db.WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.AttemptFailed))
                    .ShouldBe(1, "the consumed attempt is durably ledgered so the budget survives the suspend cycle");
            }

            await SimulateAgentExecutorAsync(secondAgent, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "Fixed on the second try." });

            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Success, "the respawned agent's success completes the run — the transient death was absorbed");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task P2_3_a_respawn_warm_resumes_the_prior_attempts_captured_session()
    {
        // P2.3: the retiring resume payload — carrying attempt 1's REAL sessionId (persisted via the same
        // AgentRunService.CompleteAsync/BuildResumePayload production path a live run uses) — rides forward as
        // PriorAttemptPayload so the respawned agent's OWN TaskJson requests a warm continuation, not a cold start.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, RetryingAgentNodeDefinition(maxAttempts: 2));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var firstAgent = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(firstAgent, new AgentRunResult
            {
                Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "rate limited", SessionId = "sess-rate-limited",
            });

            await RunEngineAsync(runId);   // resume → attempt 1 fails → the respawn stages a FRESH agent + parks again

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            var secondAgent = (await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync()).Single(a => a.Id != firstAgent);
            var task = JsonSerializer.Deserialize<AgentTask>(secondAgent.TaskJson, AgentJson.Options)!;

            task.ResumeFromSessionId.ShouldBe("sess-rate-limited", "the respawn continues attempt 1's session instead of starting a brand-new conversation");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task P3_1_a_grader_infra_timeout_respawns_instead_of_failing_terminally()
    {
        // P3.1: a fail-closed "acceptance-failed" re-grade caused by the GRADER'S OWN timeout ("tests-timed-out",
        // an environment/workload fact, not a code defect) must respawn like a crash/timeout — NOT fail terminally
        // like a genuine "the tests broke" verdict does (proven by the sibling AgentAcceptanceContract test with
        // "tests-failed-exit-1", unchanged). AcceptanceFailed mirrors AgentAcceptanceContract.FailClosed exactly.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, RetryingAgentNodeDefinition(maxAttempts: 2));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var firstAgent = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(firstAgent, new AgentRunResult
            {
                Status = AgentRunStatus.Failed, ExitReason = AgentAcceptanceContract.FailClosedExitReason,
                Error = "The acceptance check did not pass: tests-timed-out", AcceptancePassed = false, AcceptanceDetail = "tests-timed-out",
                ChangedFiles = new[] { "src/a.ts" },
            });

            await RunEngineAsync(runId);   // resume → the grader-infra fault respawns a FRESH agent, not a terminal failure

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "the respawned agent parks the run again — a grader infra fault is never terminal");
            (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                .ShouldBe(2, "the fresh respawn staged a SECOND agent run");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task The_respawn_budget_is_durable_so_a_second_transient_failure_exhausts_it()
    {
        // Retry {2}: two transient deaths consume the whole budget — the durable attempt ledger stops the loop at
        // exactly MaxAttempts staged agents (never an unbounded respawn), and the run fails honestly.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, RetryingAgentNodeDefinition(maxAttempts: 2));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var firstAgent = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(firstAgent, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "rate limited" });
            await RunEngineAsync(runId);   // respawn (attempt 2 staged)

            Guid secondAgent;
            using (var mid = _fixture.BeginScope())
            {
                var db = mid.Resolve<CodeSpaceDbContext>();
                secondAgent = (await db.AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).OrderBy(r => r.CreatedDate).ToListAsync()).Single(a => a.Id != firstAgent).Id;
            }

            await SimulateAgentExecutorAsync(secondAgent, new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "rate limited again" });
            await RunEngineAsync(runId);   // resume → durable count says attempt 2 of 2 → budget exhausted → fail

            using var verify = _fixture.BeginScope();
            var db2 = verify.Resolve<CodeSpaceDbContext>();

            (await db2.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Failure, "the second failure exhausted the durable budget");
            (await db2.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                .ShouldBe(2, "exactly MaxAttempts agents were staged — the durable ledger prevents a runaway respawn loop");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task A_needs_review_outcome_is_deterministic_and_never_respawned()
    {
        // NeedsReview parked human-owed work — respawning cannot change that verdict, so even with retry budget
        // remaining the node fails immediately and exactly ONE agent was ever staged.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, RetryingAgentNodeDefinition(maxAttempts: 3));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            var agentRunId = await GetAgentRunIdAsync(runId);

            await SimulateAgentExecutorAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.NeedsReview, ExitReason = "completed", Summary = "I need a decision on the API shape." });
            await RunEngineAsync(runId);

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();

            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Failure, "a human-owed verdict fails the node without burning the retry budget");
            (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                .ShouldBe(1, "a deterministic outcome is never respawned");
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // manual → agent.code(Retry{maxAttempts, 0s}) → terminal — the respawn tests' definition (0s backoff so tests never sleep).
    private static WorkflowDefinition RetryingAgentNodeDefinition(int maxAttempts)
    {
        var definition = AgentNodeDefinition(withErrorBranch: false);
        var nodes = definition.Nodes.Select(n => n.Id == "agent" ? n with { Retry = new RetryPolicy { MaxAttempts = maxAttempts, BackoffSeconds = 0 } } : n).ToList();

        return definition with { Nodes = nodes };
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
