using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// The durable marker that says a stream's bytes are HELD rather than lost: one producer states that its remote is
/// refusing segments transiently, and clears it the moment one commits again.
///
/// <para>A sibling of <see cref="IAgentRunLogService"/> rather than a tenth method on it (Rule 7). Every verb on that
/// seam moves the stream's monotonic head — an offset, a revision, a terminal state — and each one is something a
/// reader may treat as progress. This says the opposite: nothing moved, and it is not an accident. Widening the head
/// seam with it would have handed every decorator and double a verb that has nothing to do with appending bytes.</para>
///
/// <para><b>It changes nothing about the stream's CONTENT.</b> Not the byte head, not the source cursor, not the
/// capture claim, not the state — 0230's guard requires every one of them to be untouched, which is what keeps "my
/// bytes are queued" from ever being mistakable for "my bytes are stored". It does advance the revision, because that
/// table's guard demands it of every update and a side channel exempt from the monotonic rule is a row two writers can
/// disagree about; the statement therefore HANDS BACK the head it just moved so its caller's next fenced call still
/// carries the revision the row actually has. A failure is contained: a health marker that cannot be written must
/// never be able to break the capture it describes.</para>
/// </summary>
public interface IAgentRunLogRemoteStallWriter : IScopedDependency
{
    /// <summary>
    /// Record or clear one stream's remote stall. Both columns move together: a
    /// <see cref="AgentRunLogRemoteStallRequest.StalledSince"/> with a code says "held since", and a null pair says
    /// "the remote answered, nothing is held". Fenced on the exact worker epoch, capture session and an Open state, so
    /// a superseded worker can never restate the health of a stream it no longer owns.
    ///
    /// <para>Returns the stream's head AS OF this statement, or null when nothing was written — a restatement of the
    /// same stall, a stream this producer no longer owns, or a malformed request. Null is the ordinary answer for the
    /// first two and is not a failure to retry.</para>
    /// </summary>
    Task<AgentRunLogMetadata?> RecordRemoteStallAsync(AgentRunLogRemoteStallRequest request, CancellationToken cancellationToken);
}

/// <summary>One stream's remote-stall statement. A null <see cref="StalledSince"/> AND <see cref="StallCode"/> clears it; anything else is refused, because a stall with no cause is a shrug and a cause with no start cannot be aged out.</summary>
public sealed record AgentRunLogRemoteStallRequest
{
    public required Guid TeamId { get; init; }
    public required Guid AgentRunId { get; init; }
    public required Guid StreamId { get; init; }
    public required long WorkerFenceEpoch { get; init; }
    public required Guid CaptureSessionId { get; init; }

    /// <summary>When the remote first refused a segment this producer is still holding; null clears the stall.</summary>
    public DateTimeOffset? StalledSince { get; init; }

    /// <summary>The typed refusal being waited out, in the capture bridge's own error-code vocabulary; null clears the stall.</summary>
    public string? StallCode { get; init; }
}
