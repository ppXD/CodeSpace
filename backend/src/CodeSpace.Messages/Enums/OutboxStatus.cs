namespace CodeSpace.Messages.Enums;

public enum OutboxStatus
{
    /// <summary>Awaiting processing or backoff after a previous failure.</summary>
    Pending,

    /// <summary>
    /// Claimed by a specific worker via atomic UPDATE...RETURNING. The <c>claimed_by</c>
    /// + <c>lease_until</c> columns name the worker and the deadline. On success →
    /// Completed; on failure → back to Pending with backoff; on lease expiry without
    /// progress → reset to Pending by <c>OutboxLeaseReaper</c>.
    /// </summary>
    Claimed,

    /// <summary>Handler succeeded — terminal state.</summary>
    Completed,

    /// <summary>Handler exhausted MaxAttempts — terminal state requiring operator intervention.</summary>
    DeadLettered
}
