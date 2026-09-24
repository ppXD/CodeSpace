using System.Reflection;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// What the OWNING executor does once it discovers it no longer owns its run — pinned on the one method both of its
/// tear-down arms ask (<c>StopCancelledAttemptAsync</c>), with fakes for the row, the runner and the broker.
///
/// <para>The fresh row is the whole decision. A CANCEL of this very attempt (Cancelled, still naming this owner, one
/// epoch past it — exactly what <c>CancelRunningAsync</c>'s CAS leaves) is nobody else's to finish: the host that
/// flipped the row may be the API pod, which cannot reach this worker's process, so this pass withdraws the credential
/// and kills its own agent, and only a CONFIRMED death releases the clone. Anything else — a reclaim's reservation, a
/// re-attach that already took the run, a cancel that landed after such a reclaim, the reconciler's own abandon —
/// leaves the agent exactly as before this seam existed, for whoever owns it now.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunExecutorCancelledAttemptTests
{
    private static readonly AgentRunOwnerToken Owner = new(Guid.NewGuid(), Guid.NewGuid(), 7);

    public enum FreshRow
    {
        CancelledThisAttempt,
        ReclaimReserved,
        ReattachedElsewhere,
        CancelledAfterReclaim,
        LeaseLapsedStillRunning,
        AbandonedAfterLeaseLapse,
    }

    [Theory]
    [InlineData(FreshRow.CancelledThisAttempt, true)]      // the API pod's cancel CAS'd on this pass's epoch — nobody else will stop this agent
    [InlineData(FreshRow.ReclaimReserved, false)]          // the reconciler reserved a re-attach — the live agent is its successor's
    [InlineData(FreshRow.ReattachedElsewhere, false)]      // a re-attach already owns it
    [InlineData(FreshRow.CancelledAfterReclaim, false)]    // cancelled, but at a successor's attempt — the owning host's sweep answers that one
    [InlineData(FreshRow.LeaseLapsedStillRunning, false)]  // the lease ran out under this pass; still Running, still recoverable
    [InlineData(FreshRow.AbandonedAfterLeaseLapse, false)] // the reconciler's own terminal: this owner, one epoch on — but Failed, and its abandon owns the kill
    public async Task A_lost_fence_stops_the_agent_only_when_the_fresh_row_is_a_cancel_of_this_attempt(FreshRow shape, bool cancelledHere)
    {
        var world = new World(RowFor(shape));

        var stopped = await world.Executor.StopCancelledAttemptAsync(Owner, RecordedHandle);

        stopped.ShouldBe(cancelledHere, $"a {shape} row {(cancelledHere ? "must release" : "must not release")} the clone — true is what lets the executor dispose it instead of keeping it for a re-attach");
        world.Runner.Terminated.Count.ShouldBe(cancelledHere ? 1 : 0, $"a {shape} row {(cancelledHere ? "must be killed exactly once" : "must not be killed — the agent belongs to whoever owns the run now")}");
        world.Broker.Revoked.Count.ShouldBe(cancelledHere ? 1 : 0, $"a {shape} row {(cancelledHere ? "must withdraw this pass's brokered credential" : "must leave the credential to the executor's ordinary exit")}");

        if (!cancelledHere) return;

        var killed = world.Runner.Terminated.Single();
        (killed.ProcessId, killed.SpoolDirectory).ShouldBe((world.Handle.ProcessId, world.Handle.SpoolDirectory), "the kill must be aimed at the run's own recorded handle, never at anything else on this host");

        var revoked = world.Broker.Revoked.Single();
        (revoked.RunId, revoked.FencedToEpoch).ShouldBe((Owner.RunId, (long?)Owner.Epoch), "the withdrawal is this pass's own lease, fenced to the epoch it launched at");
    }

    [Fact]
    public async Task The_credential_is_withdrawn_before_the_kill_is_issued()
    {
        var world = new World(RowFor(FreshRow.CancelledThisAttempt));

        await world.Executor.StopCancelledAttemptAsync(Owner, RecordedHandle);

        world.Sequence.ShouldBe(["revoke", "terminate"], "a signal races the agent's next model call and a withdrawn lease does not — the same order CancelRunningAsync promises");
    }

    public enum Acknowledged
    {
        SameExecution,
        SameExecutionCheckpointedSince,
        NoneYet,
        AnotherExecution,
    }

    /// <summary>
    /// "Already gone" about the RECORDED agent proves the clone empty only while the row names the launch this pass last
    /// acknowledged. Mid-launch — a round-2 process started, its handle not yet written — the row still names round 1's
    /// finished process, whose kill answers AlreadyGone while the new process works on in the clone.
    /// </summary>
    [Theory]
    [InlineData(Acknowledged.SameExecution, true)]                    // the row names what this pass launched
    [InlineData(Acknowledged.SameExecutionCheckpointedSince, true)]   // the same process; only a checkpoint's offset moved on the row
    [InlineData(Acknowledged.NoneYet, false)]                         // a launch began whose handle never landed — the row is a round behind
    [InlineData(Acknowledged.AnotherExecution, false)]                // the row names some other launch than the one this pass acknowledged
    public async Task A_stopped_agent_frees_the_clone_only_when_the_row_names_the_launch_this_pass_acknowledged(Acknowledged acknowledged, bool frees)
    {
        var world = new World(RowFor(FreshRow.CancelledThisAttempt) with { RunnerHandleJson = JsonSerializer.Serialize(RecordedHandle with { StdoutOffset = 8192 }, AgentJson.Options) }, SandboxTerminateResult.AlreadyGone);

        var stopped = await world.Executor.StopCancelledAttemptAsync(Owner, AcknowledgedAs(acknowledged));

        world.Runner.Terminated.Count.ShouldBe(1, "the recorded agent is killed either way — the question is only what its death proves");
        stopped.ShouldBe(frees, $"with the pass's acknowledged launch {acknowledged}, AlreadyGone about the recorded agent {(frees ? "proves" : "does not prove")} that nothing is working in the clone");
    }

    private static SandboxHandle? AcknowledgedAs(Acknowledged acknowledged) => acknowledged switch
    {
        Acknowledged.SameExecution => RecordedHandle with { StdoutOffset = 8192 },
        Acknowledged.SameExecutionCheckpointedSince => RecordedHandle,
        Acknowledged.NoneYet => null,
        Acknowledged.AnotherExecution => RecordedHandle with { ProcessId = 525252, SpoolDirectory = "/spool/cancelled-attempt-r1" },
        _ => throw new ArgumentOutOfRangeException(nameof(acknowledged)),
    };

    [Theory]
    [InlineData(SandboxTerminateOutcome.TimedOutWaitingReap)]
    [InlineData(SandboxTerminateOutcome.SkippedIndeterminate)]
    [InlineData(SandboxTerminateOutcome.SkippedNotLocal)]
    public async Task A_cancel_whose_kill_is_not_confirmed_keeps_the_clone(SandboxTerminateOutcome withheld)
    {
        var world = new World(RowFor(FreshRow.CancelledThisAttempt), SandboxTerminateResult.Skipped(withheld, "fixture"));

        var stopped = await world.Executor.StopCancelledAttemptAsync(Owner, RecordedHandle);

        world.Runner.Terminated.Count.ShouldBe(1, "the kill is still attempted");
        stopped.ShouldBeFalse($"a {withheld} terminate proves nothing about the agent, so the clone it may still be working in must not be deleted under it");
    }

    [Fact]
    public async Task A_cancelled_row_with_no_recorded_handle_kills_nothing_and_keeps_the_clone()
    {
        var world = new World(RowFor(FreshRow.CancelledThisAttempt) with { RunnerHandleJson = null });

        var stopped = await world.Executor.StopCancelledAttemptAsync(Owner, RecordedHandle);

        world.Runner.Terminated.ShouldBeEmpty("there is no recorded execution to aim a kill at");
        stopped.ShouldBeFalse("no handle is not proof of no process — a launch whose handle was never acknowledged may still be running in the clone");
    }

    [Fact]
    public async Task A_fresh_row_that_cannot_be_read_stops_nothing()
    {
        var world = new World(RowFor(FreshRow.CancelledThisAttempt), readFailure: new TimeoutException("the database did not answer"));

        var stopped = await world.Executor.StopCancelledAttemptAsync(Owner, RecordedHandle);

        stopped.ShouldBeFalse("an unanswered question is not a cancel");
        world.Runner.Terminated.ShouldBeEmpty("no kill without the row that authorizes it");
        world.Broker.Revoked.ShouldBeEmpty();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly SandboxHandle RecordedHandle = new() { Kind = SandboxKinds.Local, ProcessId = 424242, SpoolDirectory = "/spool/cancelled-attempt", Deadline = DateTimeOffset.UtcNow.AddHours(1), LaunchHost = "worker-a" };

    /// <summary>The row as each actor that can take a run away from its owner leaves it — AgentRunService's CancelRunningAsync, ReserveReattachAsync and ActivateReattachAsync, and the reconciler's CasTerminalAsync (which, like the cancel, keeps the owner and bumps the epoch by one).</summary>
    private static AgentRunRow RowFor(FreshRow shape) => shape switch
    {
        FreshRow.CancelledThisAttempt => new(AgentRunStatus.Cancelled, Owner.OwnerId, null, Owner.Epoch + 1),
        FreshRow.ReclaimReserved => new(AgentRunStatus.Running, null, Guid.NewGuid(), Owner.Epoch + 1),
        FreshRow.ReattachedElsewhere => new(AgentRunStatus.Running, Guid.NewGuid(), null, Owner.Epoch + 1),
        FreshRow.CancelledAfterReclaim => new(AgentRunStatus.Cancelled, Guid.NewGuid(), null, Owner.Epoch + 2),
        FreshRow.LeaseLapsedStillRunning => new(AgentRunStatus.Running, Owner.OwnerId, null, Owner.Epoch),
        FreshRow.AbandonedAfterLeaseLapse => new(AgentRunStatus.Failed, Owner.OwnerId, null, Owner.Epoch + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private sealed record AgentRunRow(AgentRunStatus Status, Guid? OwnerId, Guid? ReservationId, long FenceEpoch)
    {
        public string? RunnerHandleJson { get; init; } = JsonSerializer.Serialize(RecordedHandle, AgentJson.Options);

        public AgentRun ToEntity() => new() { Id = Owner.RunId, TeamId = Guid.NewGuid(), Harness = "scripted", Status = Status, OwnerId = OwnerId, ReattachReservationId = ReservationId, FenceEpoch = FenceEpoch, RunnerHandleJson = RunnerHandleJson };
    }

    private sealed class World
    {
        public World(AgentRunRow row, SandboxTerminateResult? outcome = null, Exception? readFailure = null)
        {
            Handle = RecordedHandle;
            Runner = new RecordingRunner(Sequence, outcome ?? SandboxTerminateResult.Killed);
            Broker = new RecordingBroker(Sequence);

            var runs = DispatchProxy.Create<IAgentRunService, FreshRowProxy>();
            ((FreshRowProxy)runs).Answer = readFailure is null ? Task.FromResult(row.ToEntity()) : Task.FromException<AgentRun>(readFailure);

            Executor = new AgentRunExecutor(runs, null!, null!, new SandboxRunnerRegistry([Runner]), null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<AgentRunExecutor>.Instance, credentialBroker: Broker);
        }

        public List<string> Sequence { get; } = [];
        public SandboxHandle Handle { get; }
        public RecordingRunner Runner { get; }
        public RecordingBroker Broker { get; }
        public AgentRunExecutor Executor { get; }
    }

    /// <summary>The run service, reduced to the one read the classification is allowed to make: a lost-fence pass has no authority left to WRITE anything, so any other call fails the test.</summary>
    public class FreshRowProxy : DispatchProxy
    {
        public Task<AgentRun> Answer { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            method!.Name.ShouldBe(nameof(IAgentRunService.GetAsync), "a pass that lost its fence may only READ the row");
            ((Guid)args![0]!).ShouldBe(Owner.RunId);

            return Answer;
        }
    }

    public sealed class RecordingRunner(List<string> sequence, SandboxTerminateResult outcome) : ISandboxRunner, ISandboxDurableRunner
    {
        public List<SandboxHandle> Terminated { get; } = [];

        public string Kind => SandboxKinds.Local;

        public Task<SandboxTerminateResult> TerminateAsync(SandboxHandle handle, CancellationToken cancellationToken)
        {
            sequence.Add("terminate");
            Terminated.Add(handle);

            return Task.FromResult(outcome);
        }

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string spoolKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<SandboxOutputFrame, CancellationToken, Task> onStdoutFrame, CancellationToken cancellationToken, Func<long, CancellationToken, Task>? onCheckpoint = null) => throw new NotSupportedException();
        public Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException("the owner killed its own launch; a probe first would only widen the race the kill closes");
    }

    public sealed class RecordingBroker(List<string> sequence) : IModelCredentialBroker
    {
        public List<(Guid RunId, string Reason, long? FencedToEpoch)> Revoked { get; } = [];

        public Task RevokeAsync(Guid runId, string reason, long? fencedToEpoch, CancellationToken cancellationToken)
        {
            sequence.Add("revoke");
            Revoked.Add((runId, reason, fencedToEpoch));

            return Task.CompletedTask;
        }

        public Task<BrokeredModelCredential?> OpenAsync(ModelCredentialLeaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RebindAsync(ModelCredentialRebindRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RenewAsync(Guid runId, long epoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool HasLease(Guid runId) => throw new NotSupportedException();
    }
}
