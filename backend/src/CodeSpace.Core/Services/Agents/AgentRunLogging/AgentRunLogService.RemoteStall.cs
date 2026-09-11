using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <inheritdoc cref="IAgentRunLogRemoteStallWriter"/>
public sealed partial class AgentRunLogService : IAgentRunLogRemoteStallWriter
{
    private const int MaximumStallCodeLength = 128;

    public async Task<AgentRunLogMetadata?> RecordRemoteStallAsync(AgentRunLogRemoteStallRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return null;

        await using var db = CreateDb();

        if (await StallUpdateAsync(db, request, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false) == 0) return null;

        return await RequireMetadataAsync(request.TeamId, request.StreamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One fenced statement, deliberately NOT a read-modify-write through the tracked entity: the whole claim lives in
    /// the WHERE, so this cannot lose a race to a concurrent append the way a re-read would. The revision advances
    /// because 0230's guard requires every update to (a side channel exempt from the monotonic rule is a row two
    /// writers can disagree about), and the <c>IS DISTINCT FROM</c> tail makes a restatement of the same stall a
    /// zero-row no-op — so a long outage costs one write at its start and one at its end rather than one per retry.
    /// </summary>
    private static async Task<int> StallUpdateAsync(Persistence.Db.CodeSpaceDbContext db, AgentRunLogRemoteStallRequest request, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE agent_run_log_stream
               SET remote_stall_since = {request.StalledSince}::timestamptz, remote_stall_code = {request.StallCode}::text,
                   revision = revision + 1, last_modified_at = GREATEST(last_modified_at, {now}::timestamptz)
             WHERE team_id = {request.TeamId} AND id = {request.StreamId} AND agent_run_id = {request.AgentRunId}
               AND worker_fence_epoch = {request.WorkerFenceEpoch} AND capture_session_id = {request.CaptureSessionId}
               AND state = 'Open'
               AND (remote_stall_since IS DISTINCT FROM {request.StalledSince}::timestamptz OR remote_stall_code IS DISTINCT FROM {request.StallCode}::text)
            """, cancellationToken).ConfigureAwait(false);

    /// <summary>Both stall columns move together or neither does — a start with no cause is a shrug, and a cause with no start cannot be aged out against the park ceiling.</summary>
    private static bool Valid(AgentRunLogRemoteStallRequest request) =>
        request.TeamId != Guid.Empty && request.AgentRunId != Guid.Empty && request.StreamId != Guid.Empty
        && request.WorkerFenceEpoch > 0 && request.CaptureSessionId != Guid.Empty
        && (request.StalledSince == null
            ? request.StallCode == null
            : request.StallCode is { Length: > 0 and <= MaximumStallCodeLength } code && ErrorCodePattern().IsMatch(code));
}
