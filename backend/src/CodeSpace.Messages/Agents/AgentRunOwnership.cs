namespace CodeSpace.Messages.Agents;

/// <summary>Server-minted observer identity. Neither a run id nor an epoch alone confers ownership. This is not a logical execution key, physical process id, or an authority grant.</summary>
public sealed record AgentRunOwnerToken(Guid RunId, Guid OwnerId, long Epoch);

/// <summary>One reconciler reservation carried verbatim by the durable dispatch. Activation consumes it once; a duplicate job cannot read and adopt the current owner.</summary>
public sealed record AgentRunReattachReservation(Guid RunId, Guid ReservationId, long Epoch);

/// <summary>The frozen row observed before asynchronous reconciliation probes. This is comparison evidence, never a worker token or execution grant.</summary>
public sealed record AgentRunReconciliationCandidate
{
    public required Guid RunId { get; init; }
    public Guid? OwnerId { get; init; }
    public Guid? ReservationId { get; init; }
    public required long Epoch { get; init; }
    public string? RunnerHandleJson { get; init; }
    public int ReattachAttempts { get; init; }
}
