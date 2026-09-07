namespace CodeSpace.Messages.Agents;

/// <summary>Server-minted observer identity. Neither a run id nor an epoch alone confers ownership. This is not a logical execution key, physical process id, or an authority grant.</summary>
public sealed record AgentRunOwnerToken(Guid RunId, Guid OwnerId, long Epoch);

/// <summary>One reconciler reservation carried verbatim by the durable dispatch. Activation consumes it once; a duplicate job cannot read and adopt the current owner.</summary>
public sealed record AgentRunReattachReservation(Guid RunId, Guid ReservationId, long Epoch);

/// <summary>The frozen row observed before asynchronous reconciliation probes. This is comparison evidence, never a worker token or execution grant.</summary>
public sealed record AgentRunReconciliationCandidate
{
    public required Guid RunId { get; init; }
    public required Guid TeamId { get; init; }
    public Guid? OwnerId { get; init; }
    public Guid? ReservationId { get; init; }
    public required long Epoch { get; init; }
    public string? RunnerHandleJson { get; init; }
    public int ReattachAttempts { get; init; }
}

/// <summary>
/// Why the reconciler gave up on a Running Agent Run, rather than an executor's own ordinary terminal — the
/// vocabulary a closed <c>WorkflowRunHarnessProcessAttempt</c>/<c>WorkflowRunHarnessExecution</c> row needs so an
/// operator (or a later "is anything live?" reader) can tell a genuinely dead process from one nobody could
/// reach in time, rather than reading every reconciler close as the same generic "never observed" outcome.
/// </summary>
public enum AgentRunAbandonCause
{
    /// <summary>No durable process handle was ever recorded for this run (or nothing could be adopted from one), so the reconciler had no way to check whether a process was ever alive.</summary>
    NoHandle,

    /// <summary>A liveness probe against the run's recorded handle positively confirmed the process is no longer running.</summary>
    ProcessConfirmedDead,

    /// <summary>The run's lease lapsed with no worker left to renew it, and no probe could confirm either a live or a dead process in time — the run's handle could not be probed, its process outlived every re-attach attempt, or its host never answered before the run's own deadline passed.</summary>
    LeaseLapsed,
}
