using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// The one write on this seam a process that is NOT the capturing worker may perform.
///
/// <para>Every other transition here is made from inside the process doing the capture, which is why they all pin the
/// stream's exact worker fence and capture session. A host that dies makes none of them: its streams stay Open at a
/// fence the run has already moved past, and the Room folds any Open stream to "Finalizing" — so an abandoned run
/// reported a capture still in progress for as long as anyone kept looking. This closes that by saying the true thing
/// instead: the capture's owner is gone, and what it committed is all there will ever be.</para>
/// </summary>
public sealed partial class AgentRunLogService
{
    public async Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return 0;

        await using var db = CreateDb();

        // Two fences in one statement, and each refuses a different impostor. `run.fence_epoch = @epoch` is the
        // CALLER's authority: a sweep the run has already moved past states nothing. `target.worker_fence_epoch <
        // run.fence_epoch` is the SUBJECT's: only a stream whose own generation is over is orphaned, so a stream the
        // live worker still owns (equal fence) and a legacy stream with no fence at all (NULL) are both left alone.
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE agent_run_log_stream AS target SET
                state = 'CaptureFailed', error_code = {request.ErrorCode}, error_message = {OwnerLostMessage(request)},
                revision = target.revision + 1, completed_at = clock_timestamp(), last_modified_at = clock_timestamp()
            FROM agent_run AS run
            WHERE run.team_id = target.team_id AND run.id = target.agent_run_id
              AND target.team_id = {request.TeamId} AND target.agent_run_id = {request.AgentRunId}
              AND target.state = 'Open'
              AND run.fence_epoch = {request.WorkerFenceEpoch}
              AND target.worker_fence_epoch < run.fence_epoch
            """, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Says what happened and where the durable evidence of it is. The citation is the receipt's own documented
    /// identity — <c>agent_run_cleanup_receipt</c> carries no surrogate key on <c>RunCleanupReceipt</c>, and every row
    /// one abandon wrote shares exactly this run id and fence epoch — so it reads as a query an operator can run.
    /// </summary>
    private static string OwnerLostMessage(AgentRunLogOwnerLossRequest request) =>
        $"Log capture was still open when the worker that owned this run went away, and no surviving host could drain it. " +
        $"The run was terminalized at fence epoch {request.WorkerFenceEpoch}; this stream's capture generation is older than that, " +
        $"so no session can still be appending to it. Every byte already committed stays readable at the offsets recorded here — " +
        $"only whatever the source produced after the last committed segment was never captured. What the abandon left behind is " +
        $"recorded in agent_run_cleanup_receipt for agent_run_id={request.AgentRunId} at fence_epoch={request.WorkerFenceEpoch}.";

    private static bool Valid(AgentRunLogOwnerLossRequest value) =>
        value.TeamId != Guid.Empty && value.AgentRunId != Guid.Empty && value.WorkerFenceEpoch > 0 && ErrorCodePattern().IsMatch(value.ErrorCode ?? "");
}
