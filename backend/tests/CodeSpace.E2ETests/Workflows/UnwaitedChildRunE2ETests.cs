using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Reconciliation;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 E2E (high fidelity, Rule 12): a sub-workflow child that no parent waits for any more stops spending, across the real
/// pipeline — the REAL engine walks parent and child, the REAL approval resume re-walks the parent into a failing sibling,
/// the REAL Continue and stop revive or end the parent, and the REAL stuck-run reconciler, sent through the mediator as
/// the recurring job sends it, cancels what is left. Only Hangfire's transport is the in-memory client, and a parked
/// timer's wake is fired the way its scheduled job fires it.
///
/// <para>The parent: <c>start → { sub (a child that sleeps an hour), two approval gates, ready → boom (fails once) } → end</c>.
/// The child parks on its timer; approvals re-walk the parent, <c>boom</c> fails it, and the engine's failure cleanup
/// cancels only children still Pending or Enqueued — so the parked child is left with nothing waiting for it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class UnwaitedChildRunE2ETests
{
    private const int MaxTicks = 10;

    private readonly PostgresFixture _fixture;

    public UnwaitedChildRunE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_timer_parked_child_whose_parent_a_sibling_failed_is_cancelled_by_the_reconciler_and_never_walks_again()
    {
        var world = await FailParentAroundAParkedChildAsync();
        await AgeAsync(world.ChildId);

        await ReconcileUntilCancelledAsync(world.ChildId);

        var walkedBefore = await NodeStartsAsync(world.ChildId);
        (await FireTimerAsync(world.ChildId, world.TimerWaitId)).ShouldBeFalse("the cancel closed the child's timer wait, so its scheduled wake resolves nothing");
        await DrainAsync();

        (await StatusAsync(world.ChildId)).ShouldBe(WorkflowRunStatus.Cancelled);
        (await NodeStartsAsync(world.ChildId)).ShouldBe(walkedBefore, "the child never walks again — no node of it starts after the cancel");
    }

    [Fact]
    public async Task Continue_after_engine_failure_leaves_exactly_one_live_child()
    {
        // No reconcile tick anywhere in this test: the Continue alone must end the child its failed attempt left behind.
        var world = await FailParentAroundAParkedChildAsync();

        (await ContinueAsync(world.ParentId, world.TeamId)).ShouldBeTrue("precondition: the failed parent continues in place");
        await DrainAsync();

        var live = await LiveChildrenAsync(world.ParentId);
        var survivor = live.ShouldHaveSingleItem("one sub-workflow node, one live child: the revived walk's");
        survivor.ShouldNotBe(world.ChildId, "the survivor is the child the revived walk staged");
        (await StatusAsync(world.ChildId)).ShouldBe(WorkflowRunStatus.Cancelled, "the failed attempt's parked child is cancelled by the Continue itself");
        (await PendingSubworkflowTokenAsync(world.ParentId)).ShouldBe(survivor.ToString(), "and the parent waits for exactly that child");
    }

    [Fact]
    public async Task A_child_the_stop_failed_to_cancel_is_cancelled_by_the_next_reconcile()
    {
        var teamId = await SeedTeamAsync();
        var (parentId, childId) = await ParkParentOnASleepingChildAsync(teamId, SingleSubParentAsync);

        var childCancelFails = new FailFirstReadOf(childId);
        (await CancelThroughAsync(parentId, teamId, childCancelFails))!.Cancelled.ShouldBeTrue("precondition: the parent was stopped");

        childCancelFails.Failed.ShouldBe(childId, "precondition: the stop's teardown tried the child and that cancel failed");
        (await StatusAsync(childId)).ShouldBe(WorkflowRunStatus.Suspended, "precondition: the child outlived its parent's stop");

        await AgeAsync(childId);
        await ReconcileUntilCancelledAsync(childId);
    }

    // ─── Worlds ─────────────────────────────────────────────────────────────────

    private sealed record FailedParentWorld(Guid TeamId, Guid ParentId, Guid ChildId, Guid TimerWaitId);

    /// <summary>Walk the parent until its child sleeps on a timer, approve the gate, and let <c>boom</c> fail the parent.</summary>
    private async Task<FailedParentWorld> FailParentAroundAParkedChildAsync()
    {
        var teamId = await SeedTeamAsync();
        var (parentId, childId) = await ParkParentOnASleepingChildAsync(teamId, FailingSiblingParentAsync);

        await ApproveGateAsync(parentId, teamId);   // wake 1: boom runs beside the still-parked sub and fails
        await DrainAsync();

        if (await StatusAsync(parentId) == WorkflowRunStatus.Suspended)
        {
            await ApproveGateAsync(parentId, teamId);   // wake 2: the re-walk re-reads boom's failure — the park settled first in wake 1's wave
            await DrainAsync();
        }

        (await StatusAsync(parentId)).ShouldBe(WorkflowRunStatus.Failure, "precondition: a re-walk an approval drove failed the parent at boom");
        (await StatusAsync(childId)).ShouldBe(WorkflowRunStatus.Suspended, "precondition: the engine's failure cleanup leaves a parked child as it is");

        return new FailedParentWorld(teamId, parentId, childId, await PendingTimerWaitAsync(childId));
    }

    /// <summary>Run the parent built by <paramref name="createParent"/> until its child has walked into its hour-long sleep.</summary>
    private async Task<(Guid ParentId, Guid ChildId)> ParkParentOnASleepingChildAsync(Guid teamId, Func<Guid, Guid, Task<Guid>> createParent)
    {
        var userId = await OwnerAsync(teamId);
        var childWorkflowId = await CreateWorkflowAsync(teamId, userId, SleepingChildDefinition());
        var parentRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, await createParent(teamId, childWorkflowId), teamId);

        await RunEngineAsync(parentRunId);
        await DrainAsync();

        var childId = (await LiveChildrenAsync(parentRunId)).ShouldHaveSingleItem("precondition: the parent staged one child");
        (await StatusAsync(childId)).ShouldBe(WorkflowRunStatus.Suspended, "precondition: the child walked into its sleep");

        return (parentRunId, childId);
    }

    // ─── Driving ────────────────────────────────────────────────────────────────

    /// <summary>Reconcile until the child is Cancelled, at most <see cref="MaxTicks"/> times: a tick takes the oldest candidates of the whole database, so a row this test owns can wait behind strangers.</summary>
    private async Task ReconcileUntilCancelledAsync(Guid childId)
    {
        (await StatusAsync(childId)).ShouldNotBe(WorkflowRunStatus.Cancelled, "precondition: the child is live before the sweep runs");

        for (var tick = 1; tick <= MaxTicks; tick++)
        {
            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IMediator>().Send(new ReconcileStuckRunsCommand());

            if (await StatusAsync(childId) == WorkflowRunStatus.Cancelled) return;
        }

        throw new ShouldAssertException($"child {childId} was not cancelled within {MaxTicks} reconcile ticks (last status {await StatusAsync(childId)}). Each tick cancels the oldest {StuckRunReconcilerService.BatchSize} sub-workflow children no live parent waits for — check `SELECT p.status, w.status FROM workflow_run c JOIN workflow_run p ON p.id = c.parent_run_id LEFT JOIN workflow_run_wait w ON w.token = c.id::text WHERE c.id = '{childId}'`.");
    }

    /// <summary>Move the child's staging past the reconciler's grace — the wall-clock time a real orphan lives through before a tick.</summary>
    private async Task AgeAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var past = DateTimeOffset.UtcNow - StuckRunReconcilerService.UnwaitedChildStagingGrace - TimeSpan.FromMinutes(1);
        await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedDate, past));
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task DrainAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<InMemoryBackgroundJobClient>().WaitForPendingAsync();
    }

    private async Task ApproveGateAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowService>().ApproveRunAsync(runId, teamId, await OwnerAsync(teamId), approved: true, comment: null, CancellationToken.None)).ShouldBeTrue("precondition: the gate's approval resolved");
    }

    /// <summary>The child's parked timer firing — the call the engine scheduled with Hangfire when it parked.</summary>
    private async Task<bool> FireTimerAsync(Guid runId, Guid waitId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
    }

    private async Task<bool> ContinueAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None);
    }

    /// <summary>Stop the run through a scope whose database commands pass <paramref name="interceptor"/>.</summary>
    private async Task<CancelRunOutcome?> CancelThroughAsync(Guid runId, Guid teamId, IInterceptor interceptor)
    {
        DbContextOptions<CodeSpaceDbContext> production;
        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(interceptor).Options;

        using var scope = _fixture.BeginScope(b => b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance());
        return await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);
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

    // ─── Reading ────────────────────────────────────────────────────────────────

    private async Task<WorkflowRunStatus> StatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private async Task<List<Guid>> LiveChildrenAsync(Guid parentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
            .Where(r => r.ParentRunId == parentRunId && r.SourceType == WorkflowRunSourceTypes.ChildWorkflow && r.Status != WorkflowRunStatus.Success && r.Status != WorkflowRunStatus.Failure && r.Status != WorkflowRunStatus.Cancelled)
            .Select(r => r.Id).ToListAsync();
    }

    private async Task<Guid> PendingTimerWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Timer && w.Status == WorkflowWaitStatuses.Pending).Select(w => w.Id).SingleAsync();
    }

    private async Task<string?> PendingSubworkflowTokenAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Subworkflow && w.Status == WorkflowWaitStatuses.Pending).Select(w => w.Token).SingleOrDefaultAsync();
    }

    private async Task<int> NodeStartsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }

    // ─── Workflows ──────────────────────────────────────────────────────────────

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> OwnerAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == teamId && m.Role == TeamRole.Owner).Select(m => m.UserId).SingleAsync();
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "unwaited-" + Guid.NewGuid().ToString("N")[..8], Description = null, Definition = definition, Activations = new List<WorkflowActivationInput>(), Enabled = true });
    }

    // start → nap (an hour's flow.sleep) → end
    private static WorkflowDefinition SleepingChildDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            Node("start", "trigger.manual"),
            new() { Id = "nap", TypeKey = "flow.sleep", Config = WorkflowsTestSeed.Json("""{"seconds":3600}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            Node("end", "builtin.terminal"),
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "nap" }, new() { From = "nap", To = "end" } },
    };

    // start → { sub (the child), gate, gate2 (approvals), ready → boom (fails once) } → end. The first walk parks on its first
    // wave — sub and the gates suspend, ready completes — so boom first runs on the re-walk an approval drives, beside the
    // still-parked sub. That wave settles in order and the park comes first, so the failure lands on the next wake: the
    // other gate's approval re-walks the run, which re-reads boom's failure and fails the parent.
    private async Task<Guid> FailingSiblingParentAsync(Guid teamId, Guid childWorkflowId) => await CreateWorkflowAsync(teamId, await OwnerAsync(teamId), new WorkflowDefinition
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            Node("start", "trigger.manual"),
            SubNode(childWorkflowId),
            Gate("gate"),
            Gate("gate2"),
            Flaky("ready", failTimes: 0),
            Flaky("boom", failTimes: 1),
            Node("end", "builtin.terminal"),
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sub" }, new() { From = "start", To = "gate" }, new() { From = "start", To = "gate2" }, new() { From = "start", To = "ready" }, new() { From = "ready", To = "boom" },
            new() { From = "sub", To = "end" }, new() { From = "gate", To = "end" }, new() { From = "gate2", To = "end" }, new() { From = "boom", To = "end" },
        },
    });

    private static NodeDefinition Gate(string id) => new() { Id = id, TypeKey = "flow.wait_approval", Config = WorkflowsTestSeed.Json("""{"prompt":"Go on?"}"""), Inputs = WorkflowsTestSeed.EmptyJson() };

    private static NodeDefinition Flaky(string id, int failTimes) => new() { Id = id, TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"unwaited-{{id}}-{{Guid.NewGuid():N}}","failTimes":{{failTimes}}}"""), Inputs = WorkflowsTestSeed.EmptyJson() };

    // start → sub (the child) → end
    private async Task<Guid> SingleSubParentAsync(Guid teamId, Guid childWorkflowId) => await CreateWorkflowAsync(teamId, await OwnerAsync(teamId), new WorkflowDefinition
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition> { Node("start", "trigger.manual"), SubNode(childWorkflowId), Node("end", "builtin.terminal") },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "sub" }, new() { From = "sub", To = "end" } },
    });

    private static NodeDefinition SubNode(Guid childWorkflowId) => new() { Id = "sub", TypeKey = "flow.subworkflow", Config = WorkflowsTestSeed.Json($$"""{"workflowId":"{{childWorkflowId}}"}"""), Inputs = WorkflowsTestSeed.Json("""{"inputs":{}}""") };

    private static NodeDefinition Node(string id, string typeKey) => new() { Id = id, TypeKey = typeKey, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() };
}
