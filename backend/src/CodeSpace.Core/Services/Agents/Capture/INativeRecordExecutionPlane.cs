using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The two things a single live opening cannot do, as a SIBLING of <see cref="INativeRecordPlane"/> (Rule 7) rather
/// than a widening of it: RESUME the observation of a process a replaced worker was already recording, and CLOSE the
/// execution once the executor has reached a terminal.
///
/// <para>Both exist because an execution outlives an opening. A worker torn down mid-round leaves its process alive
/// and its attempt row Running — that is the durability design, not a leak — so the worker that re-attaches is
/// observing the SAME process and must record its frames against the SAME attempt, on a stream of its own. And
/// nothing was closing the execution at all, which is not merely untidy: 0137's generation gate refuses to open a
/// generation over a live predecessor, so a permanently Running row makes the Agent Run's NEXT execution
/// unrepresentable.</para>
///
/// <para>Both remain best-effort bookkeeping. The Agent Run's own status is the only outcome authority, nothing here
/// is read by completion, terminal decision, planner, oracle or model routing, and every failure is contained by the
/// caller so a run resolves exactly as it does where this plane is not deployed.</para>
/// </summary>
public interface INativeRecordExecutionPlane
{
    /// <summary>
    /// Re-enter the LIVE process of this Agent Run for a resumed observation: the same execution and the same attempt,
    /// a stream of this opening's own, the cursor the caller's observation actually resumes at
    /// (<see cref="NativeRecordCaptureRequest.ResumeSourceOffset"/>), and how far that process's frames on this channel
    /// already reach. Null ⇒ this run has no live recorded process to resume (nothing ever captured, or its attempt is
    /// already closed); capture is skipped for the re-attach and the run proceeds untouched.
    /// </summary>
    Task<NativeRecordCaptureOpening?> ReopenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Close this Agent Run's live harness execution, and any attempt still Running inside it — but only while
    /// <paramref name="expectedEpoch"/> is still the run's fence. Called on every executor path that lands the run
    /// terminal, including the forced-terminal ones, and a no-op when the run has no live execution or when this
    /// worker no longer speaks for it, so re-running it is safe.
    ///
    /// <para>An attempt it finds still Running is an observer that died inside the capture window, so closing that one
    /// also UN-STATES the run's frame expectation: nobody knows how many frames it had read and not yet made durable,
    /// and an expectation nobody could establish must not read as complete.</para>
    ///
    /// <para>The fence is not optional bookkeeping. This is reached from the completion path's
    /// already-terminal branch, and a LOST completion CAS — the reclaim-for-reattach outcome — raises the very same
    /// exception that branch swallows. Unfenced, a superseded worker would close a live execution and stamp a live
    /// attempt Lost while the worker that reclaimed the run is still observing that process, into rows 0137 makes
    /// immutable.</para>
    /// </summary>
    Task TerminalizeAsync(Guid teamId, Guid agentRunId, long expectedEpoch, CancellationToken cancellationToken);

    /// <summary>
    /// Exactly <see cref="TerminalizeAsync"/> — close the live execution and any attempt still Running inside it,
    /// fenced the same way — but for a close that is NOT the executor's own ordinary terminal: the reconciler
    /// giving up on a run with no live worker left, or a deliberate cancel (an operator's kill, or the
    /// reconciler's parent-terminal kill-wave) that best-effort terminates the process without waiting to
    /// observe its exit. Either way nothing here read this process's actual outcome, so the closed attempt is
    /// stamped with the caller's own <paramref name="cause"/> instead of the executor's generic "never observed"
    /// reason, which is what lets an operator (or a later "is anything live?" reader) tell a genuinely dead
    /// process apart from one nobody could wait for, or one that was deliberately killed.
    /// </summary>
    Task TerminalizeAbandonedAsync(Guid teamId, Guid agentRunId, long expectedEpoch, AgentRunAbandonCause cause, CancellationToken cancellationToken);
}

public sealed partial class NativeRecordPlane : INativeRecordExecutionPlane
{
    /// <summary>Reason stamped on an execution closed with no process ever appended — a launch that died between minting the identity and inserting its first attempt. 0137 admits that row only as Abandoned with a code, because there is no process whose exit could be claimed.</summary>
    public const string ExecutionUnlaunchedErrorCode = "capture.execution-unlaunched";

    /// <summary>Reason stamped on an attempt still Running when its execution closed. The observer never recorded an outcome for it, and an unknown outcome must not read as a clean one.</summary>
    public const string ProcessOutcomeUnrecordedErrorCode = "capture.exit-unrecorded";

    private const string ProcessOutcomeUnrecordedMessage = "The harness execution reached a terminal with this process still open, so its outcome was never observed rather than assumed.";

    /// <summary>Reason stamped on an attempt the reconciler abandoned with no durable process handle recorded for its run.</summary>
    public const string ReconcilerAbandonedNoHandleErrorCode = "capture.reconciler-abandoned-no-handle";

    /// <summary>Reason stamped on an attempt the reconciler abandoned after a liveness probe positively confirmed the process was gone.</summary>
    public const string ReconcilerAbandonedProcessDeadErrorCode = "capture.reconciler-abandoned-process-dead";

    /// <summary>Reason stamped on an attempt the reconciler abandoned because the run's lease lapsed with no worker left to renew it and no probe could confirm either outcome in time.</summary>
    public const string ReconcilerAbandonedLeaseLapsedErrorCode = "capture.reconciler-abandoned-lease-lapsed";

    /// <summary>Reason stamped on an attempt closed because an operator deliberately cancelled its run while it was actively Running.</summary>
    public const string CancelOperatorCancelledErrorCode = "capture.cancel-operator-cancelled";

    /// <summary>Reason stamped on an attempt closed because the reconciler's kill-wave backstop cancelled its run after the parent workflow run had already reached a terminal state.</summary>
    public const string CancelParentTerminalErrorCode = "capture.cancel-parent-terminal";

    private const string ReconcilerAbandonedNoHandleMessage = "The reconciler abandoned this run with no durable process handle ever recorded, so it had no way to check whether a process was still alive.";

    private const string ReconcilerAbandonedProcessDeadMessage = "The reconciler abandoned this run after a liveness probe against its recorded handle positively confirmed the process was no longer running.";

    private const string ReconcilerAbandonedLeaseLapsedMessage = "The reconciler abandoned this run because its lease lapsed with no worker left to renew it, and no probe could confirm either a live or a dead process before giving up.";

    private const string CancelOperatorCancelledMessage = "An operator cancelled this run while it was actively running, so its process was stopped rather than left to finish.";

    private const string CancelParentTerminalMessage = "The reconciler's kill-wave backstop cancelled this run because its parent workflow run had already reached a terminal state before the per-agent CAS ran.";

    private const string ExecutionUnlaunchedMessage = "The harness execution was closed with no process ever appended, so nothing it could have exited from was recorded.";

    public async Task<NativeRecordCaptureOpening?> ReopenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var live = await LiveProcessAsync(db, request.TeamId, request.AgentRunId, cancellationToken).ConfigureAwait(false);

        if (live is null)
        {
            _logger.LogWarning("Native record capture found no live recorded process to resume for agent run {RunId}; the re-attach streams unchanged with no native records", request.AgentRunId);

            return null;
        }

        return new NativeRecordCaptureOpening
        {
            Handle = new NativeRecordCaptureHandle
            {
                TeamId = request.TeamId, AgentRunId = request.AgentRunId, ExecutionId = live.ExecutionId,
                AttemptId = live.AttemptId, WorkerFenceEpoch = live.WorkerFenceEpoch,
                StreamId = Guid.NewGuid(), Channel = request.Channel,
                WorkflowRunId = live.WorkflowRunId,
            },
            SourceHead = request.ResumeSourceOffset,
            RecordedHead = await RecordedHeadAsync(db, request, live.AttemptId, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Both closes carry the run's own fence, and they are TWO statements rather than one transaction. A worker
    /// superseded between them closes the attempts and leaves the execution live — recoverable by whoever holds the
    /// run, and the safe direction of the two, since 0137 makes an execution's terminal state immutable while a
    /// Running row is only blocking.
    /// </summary>
    public Task TerminalizeAsync(Guid teamId, Guid agentRunId, long expectedEpoch, CancellationToken cancellationToken) =>
        TerminalizeCoreAsync(new WorkerFence(teamId, agentRunId, expectedEpoch), ProcessOutcomeUnrecordedErrorCode, ProcessOutcomeUnrecordedMessage, cancellationToken);

    public Task TerminalizeAbandonedAsync(Guid teamId, Guid agentRunId, long expectedEpoch, AgentRunAbandonCause cause, CancellationToken cancellationToken)
    {
        var (errorCode, errorMessage) = AbandonReason(cause);

        return TerminalizeCoreAsync(new WorkerFence(teamId, agentRunId, expectedEpoch), errorCode, errorMessage, cancellationToken);
    }

    /// <summary>The vocabulary behind <see cref="AgentRunAbandonCause"/> — kept here, not on the caller, for the same reason <see cref="CloseAsync"/> decides its own Lost reason from a bare exit code: the plane owns the error-code/message pairing it persists.</summary>
    private static (string ErrorCode, string ErrorMessage) AbandonReason(AgentRunAbandonCause cause) => cause switch
    {
        AgentRunAbandonCause.NoHandle => (ReconcilerAbandonedNoHandleErrorCode, ReconcilerAbandonedNoHandleMessage),
        AgentRunAbandonCause.ProcessConfirmedDead => (ReconcilerAbandonedProcessDeadErrorCode, ReconcilerAbandonedProcessDeadMessage),
        AgentRunAbandonCause.LeaseLapsed => (ReconcilerAbandonedLeaseLapsedErrorCode, ReconcilerAbandonedLeaseLapsedMessage),
        AgentRunAbandonCause.OperatorCancelled => (CancelOperatorCancelledErrorCode, CancelOperatorCancelledMessage),
        AgentRunAbandonCause.ParentTerminal => (CancelParentTerminalErrorCode, CancelParentTerminalMessage),
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unrecognized agent-run abandon cause."),
    };

    /// <summary>
    /// Shared by <see cref="TerminalizeAsync"/> and <see cref="TerminalizeAbandonedAsync"/>: close the live execution
    /// and any attempt still Running inside it, differing only in which reason lands on the closed attempt.
    /// </summary>
    private async Task TerminalizeCoreAsync(WorkerFence fence, string attemptErrorCode, string attemptErrorMessage, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var live = await LiveExecutionAsync(db, fence.TeamId, fence.AgentRunId, cancellationToken).ConfigureAwait(false);

        if (live is null) return;

        // Attempts first, in their own statement: 0137 refuses to terminalize an execution while any attempt is still
        // Running, and the guard reads the attempt rows rather than this statement's intent.
        var unobserved = await CloseRunningAttemptsAsync(db, live, fence, attemptErrorCode, attemptErrorMessage, cancellationToken).ConfigureAwait(false);

        // An attempt still Running here is an observer that died inside the capture window: it read an unknown number
        // of frames it never made durable, so what this run's record SHOULD contain stops being knowable and its
        // completeness statement must say so rather than read as satisfied over the frames that did land.
        if (unobserved > 0 && live.WorkflowRunId is { } workflowRunId)
            await MarkIndeterminateAsync(fence.TeamId, workflowRunId, cancellationToken).ConfigureAwait(false);

        if (await CloseExecutionAsync(db, live, fence, cancellationToken).ConfigureAwait(false) > 0) return;

        _logger.LogInformation("Native record plane closed no harness execution for agent run {RunId} at fence {Epoch}: the run's fence moved or another worker closed it first, so the row is left to whoever holds the run", fence.AgentRunId, fence.Epoch);
    }

    /// <summary>
    /// The process this run's re-attach is observing. It is the attempt that is still Running, because a worker
    /// tear-down leaves the process alive and its row open — which is exactly the state a resume expects to find. An
    /// attempt that is Running implies its execution is too: 0137's head arm forces Running on append, and its
    /// terminal arm refuses to close an execution over a live attempt.
    ///
    /// <para>The execution is joined for its workflow run, which the resumed handle has to STATE and the attempt row
    /// does not carry. Taking it from the execution rather than re-reading the Agent Run is what keeps a re-attached
    /// opening's scope identical to the launch opening's — the execution snapshotted that value at mint time, so the
    /// two cannot answer the same question differently.</para>
    /// </summary>
    private static async Task<LiveProcess?> LiveProcessAsync(CodeSpaceDbContext db, Guid teamId, Guid agentRunId, CancellationToken cancellationToken) =>
        await (from attempt in db.WorkflowRunHarnessProcessAttempt.AsNoTracking()
               join execution in db.WorkflowRunHarnessExecution.AsNoTracking() on attempt.ExecutionId equals execution.Id
               where attempt.TeamId == teamId && attempt.AgentRunId == agentRunId && attempt.State == HarnessProcessAttemptState.Running
               orderby attempt.AttemptOrdinal descending
               select new LiveProcess(attempt.ExecutionId, attempt.Id, attempt.WorkerFenceEpoch, execution.WorkflowRunId))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The first source position no record of THIS PROCESS on THIS CHANNEL covers — the head below which a re-delivered
    /// line is one an earlier opening already recorded.
    ///
    /// <para>Scoped to the ATTEMPT rather than to the execution because a revise round is a different process reading a
    /// different spool from its own beginning, and its stream legitimately restarts at zero. Scoped to the CHANNEL
    /// because each channel is its own cursor space that also starts at zero, so a maximum across both would answer
    /// stdout's question with stderr's head — unreachable while every opening is stdout, and exactly the kind of
    /// protection that reads as present because the parameter is there.</para>
    ///
    /// <para>Read from the RECORDS rather than from the stored reduction checkpoint, which cannot answer it: a
    /// reduction that never opened, or that stopped mid-round, leaves frames recorded with no checkpoint covering them
    /// at all. The records cannot lag themselves.</para>
    ///
    /// <para>The terminator is added only for a FINAL record, matching the step <see cref="AgentNativeRecordPump"/>
    /// advances its cursor by, so the head names the first byte no record describes. A record the reader had to CUT
    /// carried no terminator, so adding one here would name a byte the cut frame already covered and the resume would
    /// skip it — the two sides have to agree on this or the seam loses a byte per cut.</para>
    ///
    /// <para>This is the plane's ONLY read of the record table, and every resumed opening pays it — including the
    /// per-round diagnostics opening, which is why <c>ix_workflow_run_native_record_attempt</c> leads with
    /// <c>(team_id, attempt_id, channel)</c>: without the channel in the key the equality prefix is incomplete and the
    /// answer costs a walk of every frame the attempt has recorded, on every round.</para>
    /// </summary>
    private static async Task<long> RecordedHeadAsync(CodeSpaceDbContext db, NativeRecordCaptureRequest request, Guid attemptId, CancellationToken cancellationToken)
    {
        var head = await db.WorkflowRunNativeRecord.AsNoTracking()
            .Where(record => record.TeamId == request.TeamId && record.AttemptId == attemptId && record.Channel == request.Channel)
            .MaxAsync(record => (long?)(record.SourceEndOffsetBytes ?? record.SourceOffsetBytes + record.SourceLengthBytes + (record.IsFinal ? 1 : 0)), cancellationToken).ConfigureAwait(false);

        return head ?? 0;
    }

    private static async Task<LiveExecution?> LiveExecutionAsync(CodeSpaceDbContext db, Guid teamId, Guid agentRunId, CancellationToken cancellationToken) =>
        await db.WorkflowRunHarnessExecution.AsNoTracking()
            .Where(execution => execution.TeamId == teamId && execution.AgentRunId == agentRunId
                && (execution.State == HarnessExecutionState.Pending || execution.State == HarnessExecutionState.Running))
            .OrderByDescending(execution => execution.Generation)
            .Select(execution => new LiveExecution(execution.Id, execution.AttemptCount, execution.WorkflowRunId))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// An attempt still open at this point had no outcome recorded for it, so it is Lost with a reason rather than an
    /// exit nobody saw — while this worker still holds the run, and not otherwise.
    ///
    /// <para>The predicate is the RUN's current fence, deliberately not the attempt's own
    /// <c>WorkerFenceEpoch</c>: that one is the immutable fence that LAUNCHED the process, so demanding it would leave
    /// every attempt a re-attach observes unclosable — the re-attaching worker legitimately closes the process the
    /// original worker started. What must be refused is the opposite party, the worker whose run was reclaimed out from
    /// under it, and "is the run still mine" is the question that separates them.</para>
    ///
    /// <para>A pure guarded UPDATE for the same reason <see cref="CloseAsync"/> is one: the attempt's own AFTER-insert
    /// trigger already moved its parent behind EF's cached xmin. The epoch rides INSIDE the statement, as
    /// <c>PublishManifestStore.FencedUpdateAsync</c> already does, rather than being read first and trusted across the
    /// gap.</para>
    /// </summary>
    private static async Task<int> CloseRunningAttemptsAsync(CodeSpaceDbContext db, LiveExecution live, WorkerFence fence, string errorCode, string errorMessage, CancellationToken cancellationToken)
    {
        var closedAt = DateTimeOffset.UtcNow;

        return await db.WorkflowRunHarnessProcessAttempt
            .Where(attempt => attempt.TeamId == fence.TeamId && attempt.ExecutionId == live.ExecutionId && attempt.State == HarnessProcessAttemptState.Running)
            .Where(attempt => db.AgentRun.Any(run => run.Id == fence.AgentRunId && run.TeamId == fence.TeamId && run.FenceEpoch == fence.Epoch))
            .ExecuteUpdateAsync(set => set
                .SetProperty(attempt => attempt.State, HarnessProcessAttemptState.Lost)
                .SetProperty(attempt => attempt.ErrorCode, errorCode)
                .SetProperty(attempt => attempt.ErrorMessage, errorMessage)
                .SetProperty(attempt => attempt.ExitedAt, closedAt)
                .SetProperty(attempt => attempt.LastObservedAt, closedAt)
                .SetProperty(attempt => attempt.LastModifiedAt, closedAt)
                .SetProperty(attempt => attempt.Revision, attempt => attempt.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>An execution that recorded at least one process Exited; one that recorded none can only be Abandoned with a code, because 0137 reserves Exited for a row with a process behind it. Fenced on the run for the same reason the attempt close is, and answering how many rows it moved so a refusal is logged rather than assumed to be a success.</summary>
    private static async Task<int> CloseExecutionAsync(CodeSpaceDbContext db, LiveExecution live, WorkerFence fence, CancellationToken cancellationToken)
    {
        var launched = live.AttemptCount > 0;
        var closedAt = DateTimeOffset.UtcNow;

        return await db.WorkflowRunHarnessExecution
            .Where(execution => execution.TeamId == fence.TeamId && execution.Id == live.ExecutionId
                && (execution.State == HarnessExecutionState.Pending || execution.State == HarnessExecutionState.Running))
            .Where(execution => db.AgentRun.Any(run => run.Id == fence.AgentRunId && run.TeamId == fence.TeamId && run.FenceEpoch == fence.Epoch))
            .ExecuteUpdateAsync(set => set
                .SetProperty(execution => execution.State, launched ? HarnessExecutionState.Exited : HarnessExecutionState.Abandoned)
                .SetProperty(execution => execution.TerminalAt, closedAt)
                .SetProperty(execution => execution.ErrorCode, launched ? null : ExecutionUnlaunchedErrorCode)
                .SetProperty(execution => execution.ErrorMessage, launched ? null : ExecutionUnlaunchedMessage)
                .SetProperty(execution => execution.LastModifiedAt, closedAt)
                .SetProperty(execution => execution.Revision, execution => execution.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record LiveProcess(Guid ExecutionId, Guid AttemptId, long WorkerFenceEpoch, Guid? WorkflowRunId);

    private sealed record LiveExecution(Guid ExecutionId, int AttemptCount, Guid? WorkflowRunId);

    /// <summary>The claim a terminalizing worker makes about itself — "this run is still mine at this fence" — carried as a predicate on every statement rather than read once and trusted.</summary>
    private sealed record WorkerFence(Guid TeamId, Guid AgentRunId, long Epoch);
}
