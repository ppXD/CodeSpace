using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The durable seam of the incremental reduction, as a SIBLING of <see cref="INativeRecordPlane"/> (Rule 7): read the
/// checkpoint a fold resumes from, and write a batch of frames TOGETHER WITH the reduction of the prefix they
/// complete.
///
/// <para><b>Why the write is one call and not two.</b> The reduction of a live stream is folded in memory — nothing
/// re-reads the record table to rebuild it — so the stored checkpoint is the ONLY thing a replaced worker can resume
/// from, and the window between "these frames are durable" and "the checkpoint that covers them is durable" is a
/// window in which those frames' facts are lost rather than replayed. Writing both in ONE transaction closes it: the
/// stored position can neither LEAD the frames it claims (the failure 0140's guard exists to make refusable) nor LAG
/// them (the failure that would silently shorten a resumed prefix). A crash lands before both or after both.</para>
///
/// <para><b>What it costs.</b> The checkpoint rides the batch's own unit of work, so a refused checkpoint takes that
/// batch of frames down with it. That is contained exactly as a refused batch already is — capture stops for the round
/// and the run is untouched — and it is the price of the atomicity above. It cannot change what an Agent Run resolves
/// to, and no column it writes is read by completion, terminal decision, planner, oracle or model routing.</para>
/// </summary>
public interface INativeRecordReductionPlane
{
    /// <summary>The stored checkpoint of <paramref name="reducerKind"/> over this execution, or null when none was ever written — the value a fold resumes from, and the whole of what a replaced worker recovers.</summary>
    Task<HarnessReductionCheckpointV1?> ReadCheckpointAsync(Guid teamId, Guid executionId, string reducerKind, CancellationToken cancellationToken);

    /// <summary>Persist one batch of captured frames, the events and model calls projected from them, and the checkpoint of the prefix they complete — all in ONE transaction.</summary>
    Task WriteReducedAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken);
}

public sealed partial class NativeRecordPlane : INativeRecordReductionPlane
{
    public async Task<HarnessReductionCheckpointV1?> ReadCheckpointAsync(Guid teamId, Guid executionId, string reducerKind, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var stored = await db.WorkflowRunHarnessReductionCheckpoint.AsNoTracking()
            .Where(checkpoint => checkpoint.TeamId == teamId && checkpoint.ExecutionId == executionId && checkpoint.ReducerKind == reducerKind)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return stored is null ? null : Checkpoint(stored);
    }

    public async Task WriteReducedAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken)
    {
        EnsureContractual(batch);
        EnsureResumable(checkpoint);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        foreach (var capture in batch.Records) db.WorkflowRunNativeRecord.Add(RecordRow(batch.Handle, capture));
        foreach (var projection in batch.Events) db.WorkflowRunSemanticEvent.Add(EventRow(batch.Handle, projection));

        await StageModelCallsAsync(db, batch, cancellationToken).ConfigureAwait(false);

        await StageCheckpointAsync(db, batch.Handle, checkpoint, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same contract check the batch gets, for the same reason: a checkpoint the database would refuse must be caught here, where the pump contains it, rather than at commit where it would take the batch down as an opaque constraint violation.</summary>
    private static void EnsureResumable(HarnessReductionCheckpointV1 checkpoint)
    {
        var errors = checkpoint.Validate();

        if (errors.Count > 0)
            throw new NativeRecordContractException($"A reduction checkpoint does not satisfy the data contract and was not persisted: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// Stage the checkpoint onto the batch's own unit of work — TRACKED, deliberately, unlike the plane's other
    /// writes: this row has no trigger that moves it behind EF's cache, and its <c>xmin</c> concurrency token is what
    /// makes a second reducer's interleaved advance fail loudly here instead of silently overwriting the reduction
    /// (0140 holds the frontier's monotonicity, and states plainly that it authenticates nobody).
    /// </summary>
    private static async Task StageCheckpointAsync(CodeSpaceDbContext db, NativeRecordCaptureHandle handle, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken)
    {
        var stored = await db.WorkflowRunHarnessReductionCheckpoint
            .SingleOrDefaultAsync(candidate => candidate.TeamId == handle.TeamId && candidate.ExecutionId == handle.ExecutionId
                && candidate.ReducerKind == checkpoint.ReducerKind, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        if (stored is null)
        {
            db.WorkflowRunHarnessReductionCheckpoint.Add(OpenedCheckpoint(handle, checkpoint, now));
            return;
        }

        stored.PositionJson = JsonSerializer.Serialize(checkpoint.Position, AgentJson.Options);
        stored.ReducedStateJson = JsonSerializer.Serialize(checkpoint.State, AgentJson.Options);
        stored.RecordsConsumed = checkpoint.State.RecordsConsumed;
        stored.Revision += 1;
        stored.LastModifiedAt = now;
    }

    private static WorkflowRunHarnessReductionCheckpoint OpenedCheckpoint(NativeRecordCaptureHandle handle, HarnessReductionCheckpointV1 checkpoint, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = handle.TeamId, AgentRunId = handle.AgentRunId, ExecutionId = handle.ExecutionId,
        ReducerKind = checkpoint.ReducerKind, ContractVersion = checkpoint.ContractVersion,
        PositionJson = JsonSerializer.Serialize(checkpoint.Position, AgentJson.Options),
        ReducedStateJson = JsonSerializer.Serialize(checkpoint.State, AgentJson.Options),
        RecordsConsumed = checkpoint.State.RecordsConsumed,

        // Unheld, and it stays unheld: this wiring takes no reducer lease. What stands between two reducers over one
        // execution is 0140's per-stream frontier monotonicity plus this row's own concurrency token — NOT holdership,
        // which no writer here claims and no trigger could check.
        ReducerOwnerId = null, ReducerFence = 0, ReducerLeaseExpiresAt = null,
        Revision = 1, CreatedAt = now, LastModifiedAt = now,
    };

    /// <summary>The stored row read back as the contract value a fold resumes from. A payload that no longer parses raises, and the caller degrades to no reduction rather than resuming from a state it cannot read.</summary>
    private static HarnessReductionCheckpointV1 Checkpoint(WorkflowRunHarnessReductionCheckpoint stored) => new()
    {
        ContractVersion = stored.ContractVersion,
        ExecutionId = stored.ExecutionId,
        ReducerKind = stored.ReducerKind,
        Position = JsonSerializer.Deserialize<HarnessReductionPosition>(stored.PositionJson, AgentJson.Options) ?? HarnessReductionPosition.Empty,
        State = JsonSerializer.Deserialize<HarnessReducedStateV1>(stored.ReducedStateJson, AgentJson.Options)
                ?? throw new NativeRecordContractException($"The stored reduction state of execution {stored.ExecutionId} could not be read back as a reduced state."),
    };
}
