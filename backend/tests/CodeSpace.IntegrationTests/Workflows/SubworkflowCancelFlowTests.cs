using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (high fidelity, Rule 12): stopping a workflow run stops the sub-workflows it started. The stop's
/// teardown used to cancel only a child still Pending or Enqueued, so a child already walking (Running) or parked on
/// its own agents (Suspended) ran on under a Cancelled parent — its agents holding workspaces and spending the team's
/// credential, its own children with it — toward a wait the stop had already closed. The teardown now cancels every
/// child the stop captured through the same cancel, so each child's own teardown kills its agents and cancels its own
/// children in turn; a child that finished first keeps its terminal; one child's failed cancel strands neither its
/// siblings nor the parent's remaining waits; and the request that asked for the stop going away after it committed
/// strands none of it. Drives the REAL
/// <see cref="IWorkflowService.CancelRunAsync"/> over real Postgres; a REAL detached sleeper under the local durable
/// runner stands in for the child's agent process, which the child's own teardown must reap.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SubworkflowCancelFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _spoolDirs = new();
    private readonly List<int> _launchedPids = new();

    public SubworkflowCancelFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Suspended)]
    public async Task Stopping_a_run_stops_a_started_sub_workflow_together_with_its_agents_and_its_own_sub_workflow(WorkflowRunStatus started)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentRunId: null);
        var childId = await SeedChildAsync(teamId, parentId, started);
        var childAgentId = await SeedRunningAgentAsync(teamId, childId);
        var pid = await LaunchAgentProcessAsync(childAgentId);
        var grandchildId = await SeedChildAsync(teamId, childId, WorkflowRunStatus.Running);
        var grandchildAgentId = await SeedRunningAgentAsync(teamId, grandchildId);

        ProcessAlive(pid).ShouldBeTrue($"precondition: the child's agent process is running before the stop — {ProcessLiveness.Describe(pid)}");

        (await CancelAsync(parentId, teamId))!.Cancelled.ShouldBeTrue("precondition: the parent was stopped");

        (await RunStatusAsync(childId)).ShouldBe(WorkflowRunStatus.Cancelled, $"a {started} sub-workflow is the stopped run's work too — it must not run on under a Cancelled parent");
        (await AgentStatusAsync(childAgentId)).ShouldBe(AgentRunStatus.Cancelled, "the child's own teardown ran its kill-wave over the child's agents");
        (await WaitForProcessGoneAsync(pid)).ShouldBeTrue($"the child's agent process must be reaped by the child's teardown — check `ps -p {pid}`; {ProcessLiveness.Describe(pid)}");
        (await RunStatusAsync(grandchildId)).ShouldBe(WorkflowRunStatus.Cancelled, "the grandchild is reached through the child's own teardown, not the parent's");
        (await AgentStatusAsync(grandchildAgentId)).ShouldBe(AgentRunStatus.Cancelled, "and its agents with it");
        (await PendingWaitsAsync(parentId, childId, grandchildId)).ShouldBe(0, "every level's teardown closed the waits it held");
    }

    [Fact]
    public async Task A_child_whose_cancel_fails_strands_neither_its_siblings_nor_the_parents_remaining_waits()
    {
        var teamId = await SeedTeamAsync();
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentRunId: null);
        var started = new Dictionary<Guid, WorkflowRunStatus> { [await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running)] = WorkflowRunStatus.Running, [await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Pending)] = WorkflowRunStatus.Pending };
        var approvalWaitId = await SeedPendingWaitAsync(parentId, WorkflowWaitKinds.Approval, Guid.NewGuid());

        var firstChildFails = new FailFirstReadOf(started.Keys);
        (await CancelThroughAsync(parentId, teamId, CancellationToken.None, firstChildFails))!.Cancelled.ShouldBeTrue("precondition: the parent was stopped");

        var failed = firstChildFails.Failed.ShouldNotBeNull("precondition: the first child's cancel failed");
        var sibling = started.Keys.Single(id => id != failed);

        (await RunStatusAsync(sibling)).ShouldBe(WorkflowRunStatus.Cancelled, "one child's failed cancel must not strand its sibling — the single UPDATE this replaced cancelled every staged child at once");
        (await WaitStatusAsync(approvalWaitId)).ShouldBe(WorkflowWaitStatuses.Discarded, "nor the parent's remaining waits");
        (await RunStatusAsync(parentId)).ShouldBe(WorkflowRunStatus.Cancelled);
        (await RunStatusAsync(failed)).ShouldBe(started[failed], "the child whose cancel failed is left as it was: the stuck-run reconciler never dispatches a staged child under a finished parent, and nothing sweeps a started one");
    }

    [Fact]
    public async Task A_child_whose_cancel_fact_fails_to_land_leaves_no_fact_behind_for_its_sibling_to_write()
    {
        // The child's own flip rolls back with its failed run.cancelled record, but the record used to stay tracked on the
        // teardown's context, and the next sibling's save wrote it: a run.cancelled fact on a child still Running.
        var teamId = await SeedTeamAsync();
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentRunId: null);
        var children = new[] { await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running), await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running) };

        var firstFactFails = new FailFirstCancelledFactOf(children);
        (await CancelThroughAsync(parentId, teamId, CancellationToken.None, firstFactFails))!.Cancelled.ShouldBeTrue("precondition: the parent was stopped");

        var failed = firstFactFails.Failed.ShouldNotBeNull("precondition: the first child's run.cancelled record failed to insert");
        var sibling = children.Single(id => id != failed);

        (await RunStatusAsync(sibling)).ShouldBe(WorkflowRunStatus.Cancelled, "the sibling is still torn down");
        (await CancelledFactsAsync(sibling)).ShouldBe(1, "with its own run.cancelled fact");
        (await RunStatusAsync(failed)).ShouldBe(WorkflowRunStatus.Running, "the failed child's flip rolled back with its record");
        (await CancelledFactsAsync(failed)).ShouldBe(0, "so no run.cancelled fact may exist for it — the record that failed to land must not ride the sibling's save");
    }

    [Fact]
    public async Task An_agent_whose_kill_fails_strands_neither_the_other_agents_nor_the_children_nor_the_waits()
    {
        var teamId = await SeedTeamAsync();
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentRunId: null);
        var agents = new[] { await SeedRunningAgentAsync(teamId, parentId), await SeedRunningAgentAsync(teamId, parentId) };
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running);
        var approvalWaitId = await SeedPendingWaitAsync(parentId, WorkflowWaitKinds.Approval, Guid.NewGuid());

        var firstKillFails = new FailFirstReadOf(agents);
        (await CancelThroughAsync(parentId, teamId, CancellationToken.None, firstKillFails))!.Cancelled.ShouldBeTrue("precondition: the parent was stopped");

        var failed = firstKillFails.Failed.ShouldNotBeNull("precondition: the first agent's kill failed");
        var other = agents.Single(id => id != failed);

        (await AgentStatusAsync(other)).ShouldBe(AgentRunStatus.Cancelled, "one agent's failed kill must not strand the next one");
        (await RunStatusAsync(childId)).ShouldBe(WorkflowRunStatus.Cancelled, "nor the run's children");
        (await WaitStatusAsync(approvalWaitId)).ShouldBe(WorkflowWaitStatuses.Discarded, "nor its remaining waits");
        (await AgentStatusAsync(failed)).ShouldBe(AgentRunStatus.Running, "the agent whose kill failed is left to the reconciler's parent-terminal sweep");
    }

    [Fact]
    public async Task A_stop_whose_revival_check_faults_still_tears_the_run_down()
    {
        // The teardown's first read — has a Continue revived the run? — ran outside its guarded steps, so one fault there
        // skipped the whole teardown and surfaced from the committed stop.
        var teamId = await SeedTeamAsync();
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentRunId: null);
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running);
        var approvalWaitId = await SeedPendingWaitAsync(parentId, WorkflowWaitKinds.Approval, Guid.NewGuid());

        var revivalCheckFails = new FailFirstReadOf(new[] { parentId }) { Armed = false };
        var armAfterCommit = new AfterFirstCommit(() =>
        {
            revivalCheckFails.Armed = true;
            return Task.CompletedTask;
        });

        (await Record.ExceptionAsync(() => CancelThroughAsync(parentId, teamId, CancellationToken.None, armAfterCommit, revivalCheckFails))).ShouldBeNull("the stop committed; its teardown's faults are its own");

        revivalCheckFails.Failed.ShouldBe(parentId, "precondition: the first read of the run after the stop committed — the revival check — failed");
        (await RunStatusAsync(childId)).ShouldBe(WorkflowRunStatus.Cancelled, "the teardown still ran");
        (await WaitStatusAsync(approvalWaitId)).ShouldBe(WorkflowWaitStatuses.Discarded);
    }

    [Fact]
    public async Task A_stop_whose_request_is_cancelled_right_after_it_committed_still_tears_the_run_down()
    {
        var teamId = await SeedTeamAsync();
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentRunId: null);
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running);
        var approvalWaitId = await SeedPendingWaitAsync(parentId, WorkflowWaitKinds.Approval, Guid.NewGuid());

        using var request = new CancellationTokenSource();
        var goneAfterCommit = new AfterFirstCommit(() =>
        {
            request.Cancel();
            return Task.CompletedTask;
        });

        (await CancelThroughAsync(parentId, teamId, request.Token, goneAfterCommit))!.Cancelled.ShouldBeTrue("the stop committed before its request went away");

        goneAfterCommit.Fired.ShouldBeTrue("precondition: the request that asked for the stop was cancelled the moment the stop committed");
        (await RunStatusAsync(childId)).ShouldBe(WorkflowRunStatus.Cancelled, "the teardown is owed to the committed stop, not to the request that asked for it");
        (await WaitStatusAsync(approvalWaitId)).ShouldBe(WorkflowWaitStatuses.Discarded);
    }

    [Fact]
    public async Task Stopping_a_run_leaves_a_sub_workflow_that_finished_after_the_stop_captured_it_finished()
    {
        var teamId = await SeedTeamAsync();
        var parentId = await SeedRunAsync(teamId, WorkflowRunStatus.Suspended, parentRunId: null);
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running);

        var finishAfterCapture = new AfterFirstCommit(() => FinishAsync(childId));
        (await CancelThroughAsync(parentId, teamId, CancellationToken.None, finishAfterCapture))!.Cancelled.ShouldBeTrue("precondition: the parent was stopped");

        finishAfterCapture.Fired.ShouldBeTrue("precondition: the child finished after the stop's capture committed and before its teardown ran");
        (await RunStatusAsync(childId)).ShouldBe(WorkflowRunStatus.Success, "a child that finished first keeps its own terminal — the stop's teardown never regresses it to Cancelled");
    }

    // ─── Drive the real services ──────────────────────────────────────────────────

    private async Task<CancelRunOutcome?> CancelAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);
    }

    /// <summary>Stop the run through a scope whose commands and transactions pass <paramref name="interceptors"/>, on the operator request's <paramref name="cancellationToken"/> — the seam that lands a child's finish, a fault or the request going away at an exact point of the stop.</summary>
    private async Task<CancelRunOutcome?> CancelThroughAsync(Guid runId, Guid teamId, CancellationToken cancellationToken, params IInterceptor[] interceptors)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(interceptors).Options;

        using var scope = _fixture.BeginScope(b => b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance());
        return await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, cancellationToken);
    }

    /// <summary>Fails, once, the first read (while <see cref="Armed"/>) that carries one of <c>ids</c> — a child cancel's or an agent kill's first read of its target, or the teardown's first read of the stopped run.</summary>
    private sealed class FailFirstReadOf : DbCommandInterceptor
    {
        private readonly HashSet<Guid> _ids;
        private int _fired;

        public FailFirstReadOf(IEnumerable<Guid> ids) { _ids = ids.ToHashSet(); }

        public bool Armed { get; set; } = true;

        public Guid? Failed { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var id = FirstCarried(command, _ids);

            if (!Armed || id is null || Interlocked.Exchange(ref _fired, 1) == 1) return ValueTask.FromResult(result);

            Failed = id;
            throw new InvalidOperationException($"reading {id} failed");
        }
    }

    /// <summary>Fails, once, the insert of the first run.cancelled record written for one of <c>runs</c>.</summary>
    private sealed class FailFirstCancelledFactOf : DbCommandInterceptor
    {
        private readonly HashSet<Guid> _runs;
        private int _fired;

        public FailFirstCancelledFactOf(IEnumerable<Guid> runs) { _runs = runs.ToHashSet(); }

        public Guid? Failed { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        private void Fail(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO workflow_run_record", StringComparison.Ordinal) || FirstCarried(command, _runs) is not { } run || Interlocked.Exchange(ref _fired, 1) == 1) return;

            Failed = run;
            throw new InvalidOperationException($"recording that run {run} was cancelled failed");
        }
    }

    private static Guid? FirstCarried(DbCommand command, HashSet<Guid> ids) =>
        command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).OfType<Guid>().Where(ids.Contains).Select(id => (Guid?)id).FirstOrDefault();

    private async Task<int> CancelledFactsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.RunCancelled);
    }

    /// <summary>The child's own engine landing it Success — the status-guarded terminal CAS its walk writes.</summary>
    private async Task FinishAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var finished = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun
            .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Success).SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow));

        finished.ShouldBe(1, "precondition: the child was still Running when it finished");
    }

    /// <summary>Runs <c>afterCommit</c> once, right after the scope's first commit — the stop's flip, which is also where it captured the children it ends.</summary>
    private sealed class AfterFirstCommit : DbTransactionInterceptor
    {
        private readonly Func<Task> _afterCommit;
        private int _fired;

        public AfterFirstCommit(Func<Task> afterCommit) { _afterCommit = afterCommit; }

        public bool Fired => _fired == 1;

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) await _afterCommit().ConfigureAwait(false);
        }
    }

    private async Task<WorkflowRunStatus> RunStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private async Task<AgentRunStatus> AgentStatusAsync(Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == agentId).Select(r => r.Status).SingleAsync();
    }

    private async Task<string> WaitStatusAsync(Guid waitId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.Id == waitId).Select(w => w.Status).SingleAsync();
    }

    private async Task<int> PendingWaitsAsync(params Guid[] runIds)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().CountAsync(w => runIds.Contains(w.RunId) && w.Status == WorkflowWaitStatuses.Pending);
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"subcancel-{teamId:N}", Name = "Sub-workflow Cancel", Kind = TeamKind.Workspace });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>A workflow run in <paramref name="status"/> — a sub-workflow child of <paramref name="parentRunId"/> when one is given, as the sub-workflow node stages it.</summary>
    private async Task<Guid> SeedRunAsync(Guid teamId, WorkflowRunStatus status, Guid? parentRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var sourceType = parentRunId is null ? WorkflowRunSourceTypes.Manual : WorkflowRunSourceTypes.ChildWorkflow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = sourceType, ActorType = "user", ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = sourceType, ParentRunId = parentRunId,
            Status = status, StartedAt = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>A sub-workflow child in <paramref name="status"/>, and the parent's pending Subworkflow wait on it (its token is the child's id) — the wait the stop captures the child through.</summary>
    private async Task<Guid> SeedChildAsync(Guid teamId, Guid parentId, WorkflowRunStatus status)
    {
        var childId = await SeedRunAsync(teamId, status, parentId);

        await SeedPendingWaitAsync(parentId, WorkflowWaitKinds.Subworkflow, childId);
        return childId;
    }

    /// <summary>A Running agent run of <paramref name="workflowRunId"/>, held by no worker, and the run's pending AgentRun wait on it.</summary>
    private async Task<Guid> SeedRunningAgentAsync(Guid teamId, Guid workflowRunId)
    {
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var agentId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            db.AgentRun.Add(new AgentRun { Id = agentId, TeamId = teamId, WorkflowRunId = workflowRunId, NodeId = "agent", Harness = "codex-cli", Status = AgentRunStatus.Running, StartedAt = now, HeartbeatAt = now, LeaseExpiresAt = now + AgentRunLiveness.Window, FenceEpoch = 1 });
            await db.SaveChangesAsync();

            await SeedPendingWaitAsync(workflowRunId, WorkflowWaitKinds.AgentRun, agentId);
            return agentId;
        }
    }

    private async Task<Guid> SeedPendingWaitAsync(Guid runId, string waitKind, Guid token)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var waitId = Guid.NewGuid();
        db.WorkflowRunWait.Add(new WorkflowRunWait { Id = waitId, RunId = runId, NodeId = $"{waitKind}-{token:N}", IterationKey = string.Empty, WaitKind = waitKind, Token = token.ToString(), Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync();
        return waitId;
    }

    /// <summary>Launch a REAL detached sleeper under the local durable runner and record its handle on the agent run, so a cancel of that run has a live process tree to terminate. Returns the supervised pid.</summary>
    private async Task<int> LaunchAgentProcessAsync(Guid agentId)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs-subcancel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        _spoolDirs.Add(workDir);

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "sleep 300" }, WorkingDirectory = workDir, TimeoutSeconds = 300 };

        SandboxHandle handle;
        using (var scope = _fixture.BeginScope())
            handle = await ((ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind)).LaunchAsync(spec, agentId.ToString("N"), CancellationToken.None);

        _spoolDirs.Add(handle.SpoolDirectory);
        _launchedPids.Add(handle.ProcessId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunService>().SetRunnerHandleAsync(agentId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);

        return handle.ProcessId;
    }

    /// <summary>The supervised pid's liveness, read the way the PRODUCT reads it (see <see cref="ProcessLiveness"/>).</summary>
    private static bool ProcessAlive(int pid) => ProcessLiveness.IsAliveLikeTheProduct(pid);

    /// <summary>Bounded wait (5s) for the named signal: the supervised pid is gone.</summary>
    private static async Task<bool> WaitForProcessGoneAsync(int pid)
    {
        for (var i = 0; i < 50 && ProcessAlive(pid); i++) await Task.Delay(100);
        return !ProcessAlive(pid);
    }

    public void Dispose()
    {
        foreach (var pid in _launchedPids)
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
