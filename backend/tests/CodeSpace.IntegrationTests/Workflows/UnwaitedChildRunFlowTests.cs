using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Reconciliation;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (high fidelity, Rule 12): the stuck-run reconciler cancels a sub-workflow child that no live parent is
/// waiting for, and no wake sweep revives one in the meantime. Drives the REAL <see cref="ReconcileStuckRunsCommand"/>
/// through the mediator, the REAL <see cref="IWorkflowService.CancelRunAsync"/> it cancels through, and the REAL
/// Continue, over real Postgres; a REAL detached sleeper under the local durable runner stands in for a child's agent
/// process, which the child's cancel must reap.
///
/// <para>Every seeded row is a state production writes; the writer is named beside it. Every sweep here is
/// deployment-wide and bounded, so each test waits on the row it owns (<see cref="ReconcileUntilAsync"/>), never on a
/// tally; and a "left alone" claim is only made once a control row — which the sweep would have reached no earlier than
/// the owned one — shows the tick got that far.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UnwaitedChildRunFlowTests : IDisposable
{
    private const int MaxTicks = 10;

    /// <summary>A child this old is past the staging grace; the extra minute keeps clock drift out of it.</summary>
    private static readonly TimeSpan PastGrace = StuckRunReconcilerService.UnwaitedChildStagingGrace + TimeSpan.FromMinutes(1);

    private readonly PostgresFixture _fixture;
    private readonly List<string> _spoolDirs = new();
    private readonly List<int> _launchedPids = new();

    public UnwaitedChildRunFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Cancelled: no live parent waits for the child ───────────────────────────

    [Theory]
    [InlineData(WorkflowRunStatus.Failure, WorkflowRunStatus.Pending)]
    [InlineData(WorkflowRunStatus.Failure, WorkflowRunStatus.Enqueued)]
    [InlineData(WorkflowRunStatus.Failure, WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Failure, WorkflowRunStatus.Suspended)]
    [InlineData(WorkflowRunStatus.Cancelled, WorkflowRunStatus.Pending)]
    [InlineData(WorkflowRunStatus.Cancelled, WorkflowRunStatus.Enqueued)]
    [InlineData(WorkflowRunStatus.Cancelled, WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Cancelled, WorkflowRunStatus.Suspended)]
    [InlineData(WorkflowRunStatus.Success, WorkflowRunStatus.Pending)]
    [InlineData(WorkflowRunStatus.Success, WorkflowRunStatus.Enqueued)]
    [InlineData(WorkflowRunStatus.Success, WorkflowRunStatus.Running)]
    [InlineData(WorkflowRunStatus.Success, WorkflowRunStatus.Suspended)]
    public async Task A_child_no_parent_waits_for_is_cancelled_and_its_agent_killed(WorkflowRunStatus parentStatus, WorkflowRunStatus childStatus)
    {
        if (OperatingSystem.IsWindows()) return;   // the agent process is a /bin/sh sleeper under the local durable runner

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, new RunSeed(parentStatus));   // WorkflowEngine terminal / WorkflowService.CancelRunAsync
        var childId = await SeedChildAsync(teamId, parentId, childStatus, PastGrace);
        await SeedEndedParentWaitAsync(parentId, childId, parentStatus);
        var agentId = await SeedRunningAgentAsync(teamId, childId);
        var pid = await LaunchAgentProcessAsync(agentId);

        ProcessAlive(pid).ShouldBeTrue($"precondition: the child's agent process runs before the sweep — {ProcessLiveness.Describe(pid)}");

        await ReconcileUntilAsync(() => HasStatusAsync(childId, WorkflowRunStatus.Cancelled), $"the {childStatus} child {childId} of a {parentStatus} parent reaching Cancelled");

        (await AgentStatusAsync(agentId)).ShouldBe(AgentRunStatus.Cancelled, "the child is cancelled through the full cancel, whose teardown runs the kill-wave over the child's agents");
        (await WaitForProcessGoneAsync(pid)).ShouldBeTrue($"the child's agent process must be reaped by the child's teardown — check `ps -p {pid}`; {ProcessLiveness.Describe(pid)}");
    }

    [Fact]
    public async Task A_pending_wait_under_a_finished_parent_does_not_keep_its_child()
    {
        // The engine's terminal cleanup is best-effort and swallows its failure, so a Failure parent can keep its wait Pending.
        // A parent that has finished resumes nothing, whatever its waits say.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Suspended, PastGrace);
        await SeedWaitAsync(parentId, WorkflowWaitKinds.Subworkflow, childId.ToString(), WorkflowWaitStatuses.Pending);   // WorkflowEngine.TryParkAsync; CancelPendingWaitsAndChildrenAsync then failed

        await ReconcileUntilAsync(() => HasStatusAsync(childId, WorkflowRunStatus.Cancelled), $"child {childId} of a Failure parent that still holds its Pending wait reaching Cancelled");
    }

    [Fact]
    public async Task A_continued_parents_prior_attempt_child_is_cancelled()
    {
        // Continue revives the parent in place (live again) and closes the ended attempt's waits; the walk it dispatches stages
        // a fresh child. The old child is then under a LIVE parent that no longer waits for it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Pending) { Generation = 1 });   // WorkflowService.BumpToNewGenerationAsync
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running, PastGrace);
        await SeedWaitAsync(parentId, WorkflowWaitKinds.Subworkflow, childId.ToString(), WorkflowWaitStatuses.Discarded);   // WorkflowService.DiscardPendingWaitsAsync

        await ReconcileUntilAsync(() => HasStatusAsync(childId, WorkflowRunStatus.Cancelled), $"the prior attempt's child {childId} of a continued parent reaching Cancelled");
    }

    [Fact]
    public async Task A_grandchild_whose_cancel_failed_in_its_parents_teardown_is_cancelled_on_a_later_tick()
    {
        // One tick cancels the child; the child's own teardown cancels the grandchild it waits for. When that inner cancel fails,
        // the grandchild is left under a parent that has now finished — and the next tick takes it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var rootId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var childId = await SeedChildAsync(teamId, rootId, WorkflowRunStatus.Suspended, PastGrace);
        await SeedWaitAsync(rootId, WorkflowWaitKinds.Subworkflow, childId.ToString(), WorkflowWaitStatuses.Discarded);   // WorkflowEngine.CancelPendingWaitsAndChildrenAsync
        var grandchildId = await SeedChildAsync(teamId, childId, WorkflowRunStatus.Running, PastGrace);
        await SeedWaitAsync(childId, WorkflowWaitKinds.Subworkflow, grandchildId.ToString(), WorkflowWaitStatuses.Pending);   // WorkflowEngine.TryParkAsync

        var grandchildCancelFails = new FailFirstReadOf(grandchildId);
        await ReconcileUntilAsync(() => HasStatusAsync(childId, WorkflowRunStatus.Cancelled), $"child {childId} reaching Cancelled", null, grandchildCancelFails);

        grandchildCancelFails.Failed.ShouldBe(grandchildId, "precondition: the child's teardown tried the grandchild and that cancel failed");
        (await StatusAsync(grandchildId)).ShouldBe(WorkflowRunStatus.Running, "precondition: the grandchild outlived the tick that cancelled its parent");

        await ReconcileUntilAsync(() => HasStatusAsync(grandchildId, WorkflowRunStatus.Cancelled), $"grandchild {grandchildId}, under a parent that has now finished, reaching Cancelled");
    }

    [Fact]
    public async Task One_failing_cancel_does_not_stop_the_rest()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var failing = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running, PastGrace + TimeSpan.FromMinutes(1));   // older: the sweep reaches it first
        var sibling = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running, PastGrace);

        var faults = new FailingCancels(failing);
        await ReconcileUntilAsync(() => HasStatusAsync(sibling, WorkflowRunStatus.Cancelled), $"child {sibling}, the one after a child whose cancel throws, reaching Cancelled", faults);

        faults.Attempted.ShouldContain(failing, "precondition: the sweep tried the older child first and that cancel threw");
        (await StatusAsync(failing)).ShouldBe(WorkflowRunStatus.Running, "the child whose cancel failed waits for the next tick");
    }

    // ─── Left alone ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkflowRunStatus.Pending)]     // a sibling's wake flipped it to re-walk, its child's wait still Pending
    [InlineData(WorkflowRunStatus.Enqueued)]
    [InlineData(WorkflowRunStatus.Running)]     // re-walking
    [InlineData(WorkflowRunStatus.Suspended)]   // parked on the child — how production holds a live parent
    public async Task A_child_its_live_parent_waits_for_is_left_alone(WorkflowRunStatus parentStatus)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, new RunSeed(parentStatus));   // WorkflowEngine park / WorkflowResumeService flip / dispatcher / engine claim
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running, PastGrace + TimeSpan.FromMinutes(5));
        await SeedWaitAsync(parentId, WorkflowWaitKinds.Subworkflow, childId.ToString(), WorkflowWaitStatuses.Pending);   // WorkflowEngine.TryParkAsync

        await AssertLeftAloneAsync(teamId, childId, controlAge: PastGrace, $"the child of a {parentStatus} parent that waits for it");
    }

    [Fact]
    public async Task A_child_a_live_rerun_fork_waits_for_is_left_alone()
    {
        // The fork's own parent is its lineage, finished by definition; the child's parent is the fork, which lives and waits.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var forkId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Suspended) { SourceType = WorkflowRunSourceTypes.Rerun, ParentRunId = originalId });   // WorkflowService.StageForkedRunAsync, then the engine park
        var childId = await SeedChildAsync(teamId, forkId, WorkflowRunStatus.Running, PastGrace + TimeSpan.FromMinutes(5));
        await SeedWaitAsync(forkId, WorkflowWaitKinds.Subworkflow, childId.ToString(), WorkflowWaitStatuses.Pending);   // WorkflowEngine.TryParkAsync

        await AssertLeftAloneAsync(teamId, childId, controlAge: PastGrace, "the child a live rerun fork waits for");
    }

    [Theory]
    [InlineData(WorkflowRunSourceTypes.Rerun)]
    [InlineData(WorkflowRunSourceTypes.Replay)]
    public async Task A_fork_of_a_finished_run_is_left_alone(string forkSource)
    {
        // ParentRunId is also a fork's lineage. Only a sub-workflow child is ever cancelled for its parent.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var forkId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Suspended) { SourceType = forkSource, ParentRunId = originalId, Age = PastGrace + TimeSpan.FromMinutes(5) });   // WorkflowService.StageForkedRunAsync, then the engine park
        await SeedWaitAsync(forkId, WorkflowWaitKinds.Approval, Guid.NewGuid().ToString("N"), WorkflowWaitStatuses.Pending);   // WorkflowEngine.TryParkAsync

        await AssertLeftAloneAsync(teamId, forkId, controlAge: PastGrace, $"a {forkSource} fork of a finished run");
    }

    [Fact]
    public async Task A_child_inside_its_staging_grace_is_left_alone()
    {
        // The engine commits a staged child a moment before it parks the wait that names it. Inside that moment the child has
        // no wait, and the grace is what keeps the sweep off it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Running));   // WorkflowEngine claim — walking, about to park
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Pending, TimeSpan.Zero);   // staged, its wait not yet parked

        // The control is older than the owned child (a fresh child cannot sort ahead of one past the grace), so one more tick
        // after the control falls gives the owned child its turn had the grace not kept it out.
        await AssertLeftAloneAsync(teamId, childId, controlAge: PastGrace, "a child still inside its staging grace", extraTicks: 2);
    }

    // ─── Not woken while its cancel keeps failing ───────────────────────────────

    public enum WakePath { StrandedSuspended, Timer, SupervisorSelfAdvance, SupervisorInfraPark, AbandonedSupervisorAfterClaim, AbandonedSupervisorBeforeClaim }

    [Theory]
    [InlineData(WakePath.StrandedSuspended)]
    [InlineData(WakePath.Timer)]
    [InlineData(WakePath.SupervisorSelfAdvance)]
    [InlineData(WakePath.SupervisorInfraPark)]
    [InlineData(WakePath.AbandonedSupervisorAfterClaim)]
    [InlineData(WakePath.AbandonedSupervisorBeforeClaim)]
    public async Task An_orphan_whose_cancel_keeps_failing_is_not_woken(WakePath path)
    {
        // Until its cancel lands, an orphan must not be re-walked by any of the sweeps that wake a stuck run. Its cancel is made
        // to fail so the orphan stays put; a top-level control in the same stuck shape, due after the orphan, proves the tick
        // reached that sweep's batch past the orphan's place in it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = path is WakePath.AbandonedSupervisorBeforeClaim ? await CreateSupervisorWorkflowAsync(teamId, userId) : (Guid?)null;
        var parentId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var orphanId = await SeedStuckAsync(path, teamId, workflowId, parentId, overdueBy: TimeSpan.FromMinutes(20));
        await SeedWaitAsync(parentId, WorkflowWaitKinds.Subworkflow, orphanId.ToString(), WorkflowWaitStatuses.Discarded);   // WorkflowEngine.CancelPendingWaitsAndChildrenAsync
        var controlId = await SeedStuckAsync(path, teamId, workflowId, parentRunId: null, overdueBy: TimeSpan.FromMinutes(10));

        var faults = new FailingCancels(orphanId);
        await ReconcileUntilAsync(() => WokenAsync(path, controlId), $"the {path} sweep waking control run {controlId}", faults);

        faults.Attempted.ShouldContain(orphanId, "precondition: the sweep tried to cancel the orphan and that cancel failed");
        (await WokenAsync(path, orphanId)).ShouldBeFalse($"the {path} sweep must not wake a sub-workflow child whose parent has finished — nothing is waiting for it");
    }

    // ─── Continue ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkflowRunStatus.Failure)]
    [InlineData(WorkflowRunStatus.Cancelled)]
    public async Task Continue_cancels_the_ended_attempts_started_child_through_the_full_cancel(WorkflowRunStatus ended)
    {
        if (OperatingSystem.IsWindows()) return;

        // The engine's failure cleanup cancels only a child still Pending or Enqueued, and a stop whose child cancel failed
        // leaves one started. Continue revives the parent and its re-walk stages a fresh child, so the old one is cancelled —
        // with its agents — on the Continue itself, no reconcile tick in between.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateParentWorkflowAsync(teamId, userId);
        var parentId = await SeedRunAsync(teamId, new RunSeed(ended) { WorkflowId = workflowId });   // WorkflowEngine terminal / WorkflowService.CancelRunAsync
        await RecordEndedAttemptAsync(parentId, ended);
        var childId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Suspended, TimeSpan.Zero);
        await SeedWaitAsync(parentId, WorkflowWaitKinds.Subworkflow, childId.ToString(), WorkflowWaitStatuses.Discarded);   // CancelPendingWaitsAndChildrenAsync / the stop's CancelPendingWaitsAsync
        var agentId = await SeedRunningAgentAsync(teamId, childId);
        var pid = await LaunchAgentProcessAsync(agentId);

        (await ContinueAsync(parentId, teamId)).ShouldBeTrue("precondition: the ended parent continued in place");

        (await StatusAsync(childId)).ShouldBe(WorkflowRunStatus.Cancelled, $"the {ended} attempt's started child is cancelled by the Continue — nothing waits for it once the revived walk stages its own");
        (await AgentStatusAsync(agentId)).ShouldBe(AgentRunStatus.Cancelled, "through the full cancel, whose teardown kills the child's agents");
        (await WaitForProcessGoneAsync(pid)).ShouldBeTrue($"the child's agent process must be reaped — check `ps -p {pid}`; {ProcessLiveness.Describe(pid)}");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Failure, false)]
    [InlineData(WorkflowRunStatus.Cancelled, false)]
    [InlineData(WorkflowRunStatus.Success, false)]
    [InlineData(WorkflowRunStatus.Suspended, true)]   // control: a live parent keeps its child continuable
    public async Task Continue_refuses_a_failed_child_whose_parent_has_finished(WorkflowRunStatus parentStatus, bool continues)
    {
        // Continuing it would report success and bump its generation, and then the dispatcher would refuse the run and the
        // reconciler cancel it: nothing waits for a child once its parent has finished.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateParentWorkflowAsync(teamId, userId);
        var parentId = await SeedRunAsync(teamId, new RunSeed(parentStatus));   // WorkflowEngine terminal / park
        var childId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure) { SourceType = WorkflowRunSourceTypes.ChildWorkflow, ParentRunId = parentId, WorkflowId = workflowId });   // SubworkflowService.StageChildRunAsync, then the engine's failure
        await RecordEndedAttemptAsync(childId, WorkflowRunStatus.Failure);

        (await ContinueAsync(childId, teamId)).ShouldBe(continues);

        var status = await StatusAsync(childId);

        if (continues) status.ShouldNotBe(WorkflowRunStatus.Failure, "a live parent's child revives");
        else status.ShouldBe(WorkflowRunStatus.Failure, "a refused continue leaves the child as it ended");
    }

    // ─── Isolation and rotation ─────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_find_does_not_stop_the_other_sweeps()
    {
        // The unwaited-child sweep is one sweep among several; its failure is logged and the tick still completes.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var controlId = await SeedStuckAsync(WakePath.StrandedSuspended, teamId, null, parentRunId: null, overdueBy: TimeSpan.FromMinutes(10));
        var brokenFind = new ThrowOnUnwaitedChildFind();

        await ReconcileUntilAsync(() => WokenAsync(WakePath.StrandedSuspended, controlId), $"the stranded-Suspended sweep waking control run {controlId}", null, brokenFind);

        brokenFind.Fired.ShouldBeTrue("precondition: the unwaited-child find ran and threw");
    }

    [Fact]
    public async Task A_child_whose_cancel_keeps_failing_does_not_hold_the_batch_against_a_newer_one()
    {
        // A full batch of children whose cancel fails sorts ahead of the one after them; a failed attempt moves a child to the
        // back, so the next tick reaches the newer child.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var parentId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var stuck = new List<Guid>();

        for (var i = 0; i < StuckRunReconcilerService.BatchSize; i++)
            stuck.Add(await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running, PastGrace + TimeSpan.FromMinutes(5)));

        var newer = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running, PastGrace);

        var faults = new FailingCancels(stuck.ToArray());
        await ReconcileUntilAsync(() => HasStatusAsync(newer, WorkflowRunStatus.Cancelled), $"child {newer}, behind {stuck.Count} children whose cancel throws, reaching Cancelled", faults);

        (await StatusAsync(stuck[0])).ShouldBe(WorkflowRunStatus.Running, "precondition: the children whose cancel threw are still live");
    }

    // ─── Waiting ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reconcile until <paramref name="done"/> holds for the row the test owns, at most <see cref="MaxTicks"/> times. The predicate
    /// must start false, or the wait waits for nothing. Each tick runs through the mediator as the recurring job sends it, with
    /// <paramref name="faults"/>' cancels throwing and <paramref name="interceptors"/> on every database command.
    /// </summary>
    private async Task ReconcileUntilAsync(Func<Task<bool>> done, string signal, FailingCancels? faults = null, params IInterceptor[] interceptors)
    {
        (await done()).ShouldBeFalse($"precondition: {signal} must not have happened before the sweep runs");

        for (var tick = 1; tick <= MaxTicks; tick++)
        {
            await ReconcileAsync(faults, interceptors);

            if (await done()) return;
        }

        throw new ShouldAssertException($"{signal}: not observed within {MaxTicks} reconcile ticks. Each tick takes the oldest {StuckRunReconcilerService.BatchSize} candidates of each sweep across the whole shared database, so look for rows that keep failing ahead of this one — `SELECT id, status, created_date FROM workflow_run WHERE source_type = 'workflow.child' AND status IN ('Pending','Enqueued','Running','Suspended') ORDER BY created_date`.");
    }

    /// <summary>
    /// The row is never touched while the sweep demonstrably reaches its place: an unwaited control child, due no earlier than the
    /// owned row, is cancelled first, then <paramref name="extraTicks"/> more ticks run.
    /// </summary>
    private async Task AssertLeftAloneAsync(Guid teamId, Guid runId, TimeSpan controlAge, string what, int extraTicks = 1)
    {
        var parentId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Failure));   // WorkflowEngine terminal
        var controlId = await SeedChildAsync(teamId, parentId, WorkflowRunStatus.Running, controlAge);
        var statusBefore = await StatusAsync(runId);
        var versionBefore = await RowVersionAsync(runId);

        await ReconcileUntilAsync(() => HasStatusAsync(controlId, WorkflowRunStatus.Cancelled), $"control child {controlId} reaching Cancelled");

        for (var tick = 0; tick < extraTicks; tick++) await ReconcileAsync(null);

        (await RowVersionAsync(runId)).ShouldBe(versionBefore, $"{what} must not be written at all");
        (await StatusAsync(runId)).ShouldBe(statusBefore);
    }

    private async Task ReconcileAsync(FailingCancels? faults, params IInterceptor[] interceptors)
    {
        using var manual = JobClient().ManualExecution();   // record dispatches; no engine walk runs behind the sweep
        using var scope = _fixture.BeginScope(b => ConfigureTick(b, faults, interceptors));

        await scope.Resolve<IMediator>().Send(new ReconcileStuckRunsCommand());
    }

    private void ConfigureTick(ContainerBuilder builder, FailingCancels? faults, IInterceptor[] interceptors)
    {
        if (faults != null) builder.Register(c => faults.Wrap(c.Resolve<WorkflowService>())).As<IWorkflowService>().InstancePerLifetimeScope();

        if (interceptors.Length == 0) return;

        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        builder.RegisterInstance(new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(interceptors).Options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    }

    private InMemoryBackgroundJobClient JobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    // ─── Wake paths ─────────────────────────────────────────────────────────────

    /// <summary>A run stuck the way <paramref name="path"/>'s sweep wakes, <paramref name="overdueBy"/> past that sweep's threshold — a sub-workflow child of <paramref name="parentRunId"/>, or a top-level run when it is null.</summary>
    private async Task<Guid> SeedStuckAsync(WakePath path, Guid teamId, Guid? workflowId, Guid? parentRunId, TimeSpan overdueBy)
    {
        var source = parentRunId is null ? WorkflowRunSourceTypes.Manual : WorkflowRunSourceTypes.ChildWorkflow;
        var now = DateTimeOffset.UtcNow;

        return path switch
        {
            WakePath.StrandedSuspended => await SeedStrandedAsync(teamId, source, parentRunId, StuckRunReconcilerService.SuspendedStrandedAfter + overdueBy),
            WakePath.Timer => await SeedParkedAsync(teamId, source, parentRunId, w => { w.WaitKind = WorkflowWaitKinds.Timer; w.WakeAt = now - StuckRunReconcilerService.TimerWakeLostAfter - overdueBy; }),
            WakePath.SupervisorSelfAdvance => await SeedParkedAsync(teamId, source, parentRunId, w => { w.WaitKind = WorkflowWaitKinds.SupervisorDecision; w.NodeId = "sup"; w.IterationKey = "sup#turn1"; w.CreatedAt = now - StuckRunReconcilerService.SupervisorAdvanceLostAfter - overdueBy; }),
            WakePath.SupervisorInfraPark => await SeedParkedAsync(teamId, source, parentRunId, w => { w.WaitKind = WorkflowWaitKinds.SupervisorInfraPark; w.NodeId = "sup"; w.IterationKey = "sup#infra1"; w.WakeAt = now - StuckRunReconcilerService.SupervisorInfraParkWakeLostAfter - overdueBy; w.PayloadJson = InfraParkMarker(); }),
            WakePath.AbandonedSupervisorAfterClaim => await SeedAbandonedSupervisorAsync(teamId, new RunSeed(WorkflowRunStatus.Running) { SourceType = source, ParentRunId = parentRunId, StartedAgo = StuckRunReconcilerService.RunningStuckAfter + overdueBy }, claimed: true),
            _ => await SeedAbandonedSupervisorAsync(teamId, new RunSeed(WorkflowRunStatus.Running) { SourceType = source, ParentRunId = parentRunId, WorkflowId = workflowId, StartedAgo = StuckRunReconcilerService.RunningStuckAfter + overdueBy }, claimed: false),
        };
    }

    /// <summary>Suspended with no Pending wait — its one wait closed — and untouched for <paramref name="idle"/>.</summary>
    private async Task<Guid> SeedStrandedAsync(Guid teamId, string source, Guid? parentRunId, TimeSpan idle)
    {
        var runId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Suspended) { SourceType = source, ParentRunId = parentRunId, Age = idle, LastModifiedAgo = idle });   // the engine's park, then a resume that resolved the wait without the flip
        await SeedWaitAsync(runId, WorkflowWaitKinds.Approval, Guid.NewGuid().ToString("N"), WorkflowWaitStatuses.Resolved);   // WorkflowResumeService resolve
        return runId;
    }

    /// <summary>Suspended on one Pending wait shaped by <paramref name="shape"/>.</summary>
    private async Task<Guid> SeedParkedAsync(Guid teamId, string source, Guid? parentRunId, Action<WorkflowRunWait> shape)
    {
        var runId = await SeedRunAsync(teamId, new RunSeed(WorkflowRunStatus.Suspended) { SourceType = source, ParentRunId = parentRunId, Age = TimeSpan.FromHours(1) });   // WorkflowEngine park

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var wait = new WorkflowRunWait { Id = Guid.NewGuid(), RunId = runId, NodeId = "delay", IterationKey = string.Empty, WaitKind = WorkflowWaitKinds.Timer, Token = Guid.NewGuid().ToString("N"), Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow };
        shape(wait);
        db.WorkflowRunWait.Add(wait);   // WorkflowEngine.TryParkAsync (a Timer / SupervisorInfraPark wait carries its deadline in wake_at)

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>A supervisor run whose worker died mid-turn: Running, started long ago, no ledger activity since — with a claimed, unfinished decision, or, before any claim, its supervisor node started.</summary>
    private async Task<Guid> SeedAbandonedSupervisorAsync(Guid teamId, RunSeed seed, bool claimed)
    {
        var runId = await SeedRunAsync(teamId, seed with { Age = seed.StartedAgo!.Value });   // the engine's Enqueued → Running claim

        if (claimed) await SeedRunningDecisionAsync(teamId, runId);
        else await RecordStaleNodeStartedAsync(runId, "sup", seed.StartedAgo!.Value);

        return runId;
    }

    private async Task SeedRunningDecisionAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, DecisionKind = SupervisorDecisionKinds.Stop, IdempotencyKey = $"stop:{Guid.NewGuid():N}", InputHash = Guid.NewGuid().ToString("N"), PayloadJson = "{}", Status = SupervisorDecisionStatus.Running });   // SupervisorDecisionLog claim

        await db.SaveChangesAsync();
    }

    /// <summary>The engine's node.started record for <paramref name="nodeId"/>, as old as the dead worker that wrote it. The ledger is immutable, so the backdate lifts its trigger for the one statement.</summary>
    private async Task RecordStaleNodeStartedAsync(Guid runId, string nodeId, TimeSpan ago)
    {
        using var scope = _fixture.BeginScope();
        var empty = new Dictionary<string, JsonElement>();
        await scope.Resolve<IRunRecordLogger>().NodeStartedAsync(runId, nodeId, string.Empty, empty, empty, CancellationToken.None);   // WorkflowEngine.ExecuteNodeAsync

        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_run_record DISABLE TRIGGER workflow_run_record_enforce_immutability");
        try
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE workflow_run_record SET occurred_at = {0} WHERE run_id = {1}", DateTimeOffset.UtcNow - ago, runId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_run_record ENABLE TRIGGER workflow_run_record_enforce_immutability");
        }
    }

    /// <summary>Whether <paramref name="path"/>'s sweep has acted on the run: revived it, resolved its overdue wait, or recorded a recovery of it.</summary>
    private async Task<bool> WokenAsync(WakePath path, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return path switch
        {
            WakePath.StrandedSuspended => await db.WorkflowRun.AsNoTracking().AnyAsync(r => r.Id == runId && r.Status != WorkflowRunStatus.Suspended),
            WakePath.AbandonedSupervisorAfterClaim or WakePath.AbandonedSupervisorBeforeClaim => await db.WorkflowRunRecord.AsNoTracking().AnyAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.SupervisorRunRecovered),
            _ => await db.WorkflowRunWait.AsNoTracking().AnyAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Resolved),
        };
    }

    /// <summary>The ladder marker <c>AgentSupervisorNode.ParkForInfraOrStopAsync</c> stores as both the park's payload and its deadline payload.</summary>
    private static string InfraParkMarker() => JsonSerializer.Serialize(new Dictionary<string, object?> { ["infraPark"] = true, ["parks"] = 1, ["firstParkedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("o"), ["error"] = "gateway 429 (seeded)" });

    // ─── Seeding ────────────────────────────────────────────────────────────────

    /// <summary>A workflow_run row. Unset fields are what the run starter writes for a fresh top-level run.</summary>
    private sealed record RunSeed(WorkflowRunStatus Status)
    {
        public string SourceType { get; init; } = WorkflowRunSourceTypes.Manual;
        public Guid? ParentRunId { get; init; }
        public Guid? WorkflowId { get; init; }
        public TimeSpan Age { get; init; }
        public TimeSpan? StartedAgo { get; init; }
        public TimeSpan? LastModifiedAgo { get; init; }
        public int Generation { get; init; }
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, RunSeed seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = seed.WorkflowId, SourceType = seed.SourceType, ActorType = "user", ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, WorkflowId = seed.WorkflowId, WorkflowVersion = seed.WorkflowId is null ? null : 1,
            SourceType = seed.SourceType, ParentRunId = seed.ParentRunId, Status = seed.Status, Generation = seed.Generation,
            CreatedDate = now - seed.Age, LastModifiedDate = now - (seed.LastModifiedAgo ?? TimeSpan.Zero),
            EnqueuedAt = seed.Status == WorkflowRunStatus.Enqueued ? now - seed.Age : null,
            StartedAt = seed.StartedAgo is { } started ? now - started : null,
            CompletedAt = WorkflowRunState.IsTerminal(seed.Status) ? now : null,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>A sub-workflow child of <paramref name="parentRunId"/> staged <paramref name="age"/> ago (SubworkflowService.StageChildRunAsync), carried on to <paramref name="status"/> by the dispatcher and the engine.</summary>
    private Task<Guid> SeedChildAsync(Guid teamId, Guid parentRunId, WorkflowRunStatus status, TimeSpan age) =>
        SeedRunAsync(teamId, new RunSeed(status) { SourceType = WorkflowRunSourceTypes.ChildWorkflow, ParentRunId = parentRunId, Age = age, StartedAgo = status is WorkflowRunStatus.Running or WorkflowRunStatus.Suspended ? age : null });

    /// <summary>
    /// The parent's wait on the child, as the parent's end left it: closed Discarded by a Failure's cleanup or a stop's teardown,
    /// and gone under a Success — which needs the wait dropped before the end, as the engine's re-stage of a still-parked cell
    /// (<c>ReplaceCellWaitAsync</c>) drops it.
    /// </summary>
    private async Task SeedEndedParentWaitAsync(Guid parentRunId, Guid childRunId, WorkflowRunStatus parentStatus)
    {
        if (parentStatus == WorkflowRunStatus.Success) return;

        await SeedWaitAsync(parentRunId, WorkflowWaitKinds.Subworkflow, childRunId.ToString(), WorkflowWaitStatuses.Discarded);   // WorkflowEngine.CancelPendingWaitsAndChildrenAsync / WorkflowService.CancelPendingWaitsAsync
    }

    private async Task<Guid> SeedWaitAsync(Guid runId, string waitKind, string token, string status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var waitId = Guid.NewGuid();
        var closed = status != WorkflowWaitStatuses.Pending;
        db.WorkflowRunWait.Add(new WorkflowRunWait { Id = waitId, RunId = runId, NodeId = $"{waitKind}-{token}", IterationKey = string.Empty, WaitKind = waitKind, Token = token, Status = status, PayloadJson = closed ? "{}" : null, CreatedAt = DateTimeOffset.UtcNow, ResolvedAt = closed ? DateTimeOffset.UtcNow : null });

        await db.SaveChangesAsync();
        return waitId;
    }

    /// <summary>A Running agent run of <paramref name="workflowRunId"/>, held by no worker (AgentRunExecutor's claim), and the run's pending AgentRun wait on it (WorkflowEngine.TryParkAsync).</summary>
    private async Task<Guid> SeedRunningAgentAsync(Guid teamId, Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.AgentRun.Add(new AgentRun { Id = agentId, TeamId = teamId, WorkflowRunId = workflowRunId, NodeId = "agent", Harness = "codex-cli", Status = AgentRunStatus.Running, StartedAt = now, HeartbeatAt = now, LeaseExpiresAt = now + AgentRunLiveness.Window, FenceEpoch = 1 });
        db.WorkflowRunWait.Add(new WorkflowRunWait { Id = Guid.NewGuid(), RunId = workflowRunId, NodeId = "agent", IterationKey = string.Empty, WaitKind = WorkflowWaitKinds.AgentRun, Token = agentId.ToString(), Status = WorkflowWaitStatuses.Pending, CreatedAt = now });

        await db.SaveChangesAsync();
        return agentId;
    }

    /// <summary>The records the parent's walk left: its sub-workflow step parked, and — when the run failed — the sibling step that failed it.</summary>
    private async Task RecordEndedAttemptAsync(Guid runId, WorkflowRunStatus ended)
    {
        using var scope = _fixture.BeginScope();
        var records = scope.Resolve<IRunRecordLogger>();
        var empty = new Dictionary<string, JsonElement>();

        await records.NodeStartedAsync(runId, "sub", string.Empty, empty, empty, CancellationToken.None);   // WorkflowEngine.ExecuteNodeAsync
        await records.NodeSuspendedAsync(runId, "sub", string.Empty, WorkflowWaitKinds.Subworkflow, null, CancellationToken.None);   // WorkflowEngine.TryParkAsync

        if (ended != WorkflowRunStatus.Failure) return;

        await records.NodeStartedAsync(runId, "boom", string.Empty, empty, empty, CancellationToken.None);   // WorkflowEngine.ExecuteNodeAsync
        await records.NodeFailedAsync(runId, "boom", string.Empty, "boom", TimeSpan.FromMilliseconds(1), CancellationToken.None);   // WorkflowEngine node failure
    }

    // start → { sub (a sub-workflow), boom } → end
    private async Task<Guid> CreateParentWorkflowAsync(Guid teamId, Guid userId)
    {
        var childWorkflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        return await CreateWorkflowAsync(teamId, userId, new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "sub", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""), Inputs = WorkflowsTestSeed.Json("""{"inputs":{}}""") },
                new() { Id = "boom", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{Guid.NewGuid():N}}","failTimes":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "sub" }, new() { From = "start", To = "boom" }, new() { From = "sub", To = "end" }, new() { From = "boom", To = "end" } },
        });
    }

    // start → sup (agent.supervisor) → end
    private Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId) => CreateWorkflowAsync(teamId, userId, new WorkflowDefinition
    {
        SchemaVersion = 1,
        CompletionMode = WorkflowDefinition.CompletionModeShadow,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "sup" }, new() { From = "sup", To = "end" } },
    });

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "unwaited-" + Guid.NewGuid().ToString("N")[..8], Description = null, Definition = definition, Activations = new List<WorkflowActivationInput>(), Enabled = true });
    }

    /// <summary>Launch a REAL detached sleeper under the local durable runner and record its handle on the agent run, so the agent's cancel has a live process tree to terminate. Returns the supervised pid.</summary>
    private async Task<int> LaunchAgentProcessAsync(Guid agentId)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs-unwaited-test-" + Guid.NewGuid().ToString("N"));
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

    // ─── Reading ────────────────────────────────────────────────────────────────

    private async Task<bool> ContinueAsync(Guid runId, Guid teamId)
    {
        using var manual = JobClient().ManualExecution();   // the revived walk's dispatch is recorded, not run
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<WorkflowRunStatus> StatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private async Task<bool> HasStatusAsync(Guid runId, WorkflowRunStatus status) => await StatusAsync(runId) == status;

    /// <summary>The Postgres row version. Changes on any write, so it answers "was this row touched" rather than "what does it say now".</summary>
    private async Task<uint> RowVersionAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Xmin).SingleAsync();
    }

    private async Task<AgentRunStatus> AgentStatusAsync(Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == agentId).Select(r => r.Status).SingleAsync();
    }

    private static bool ProcessAlive(int pid) => ProcessLiveness.IsAliveLikeTheProduct(pid);

    /// <summary>Bounded wait (5s) for the named signal: the supervised pid is gone.</summary>
    private static async Task<bool> WaitForProcessGoneAsync(int pid)
    {
        for (var i = 0; i < 50 && ProcessAlive(pid); i++) await Task.Delay(100);
        return !ProcessAlive(pid);
    }

    // ─── Faults ─────────────────────────────────────────────────────────────────

    /// <summary>Cancels of these runs throw, through the workflow service the reconciler cancels with; every attempt is recorded.</summary>
    public sealed class FailingCancels
    {
        private readonly HashSet<Guid> _failing;

        public FailingCancels(params Guid[] failing) { _failing = failing.ToHashSet(); }

        public ConcurrentQueue<Guid> Attempted { get; } = new();

        public IWorkflowService Wrap(IWorkflowService inner)
        {
            var proxy = DispatchProxy.Create<IWorkflowService, FailingCancelProxy>();
            ((FailingCancelProxy)(object)proxy).Bind(inner, this);
            return proxy;
        }

        internal bool Fails(Guid runId)
        {
            Attempted.Enqueue(runId);
            return _failing.Contains(runId);
        }
    }

    public class FailingCancelProxy : DispatchProxy
    {
        private IWorkflowService _inner = default!;
        private FailingCancels _faults = default!;

        internal void Bind(IWorkflowService inner, FailingCancels faults)
        {
            _inner = inner;
            _faults = faults;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowService.CancelRunAsync) && _faults.Fails((Guid)args![0]!))
                return Task.FromException<CancelRunOutcome?>(new InvalidOperationException($"cancelling run {args[0]} failed"));

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    /// <summary>Throws on every read the unwaited-child find issues (the one query that matches a wait token against a run id under NOT EXISTS).</summary>
    private sealed class ThrowOnUnwaitedChildFind : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;

            if (!sql.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase) || !sql.Contains("token", StringComparison.OrdinalIgnoreCase)) return ValueTask.FromResult(result);

            Fired = true;
            throw new InvalidOperationException("the unwaited-child find failed");
        }
    }

    /// <summary>Fails, once, the first read that carries <c>runId</c> — a cancel's first read of its target.</summary>
    private sealed class FailFirstReadOf : DbCommandInterceptor
    {
        private readonly Guid _runId;
        private int _fired;

        public FailFirstReadOf(Guid runId) { _runId = runId; }

        public Guid? Failed { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var carried = command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid id && id == _runId);

            if (!carried || Interlocked.Exchange(ref _fired, 1) == 1) return ValueTask.FromResult(result);

            Failed = _runId;
            throw new InvalidOperationException($"reading {_runId} failed");
        }
    }

    public void Dispose()
    {
        foreach (var pid in _launchedPids)
            try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }

        foreach (var dir in _spoolDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
