namespace CodeSpace.Messages.Failures;

/// <summary>
/// What KIND of failure something is, in the domain's own terms — deliberately not an HTTP status.
///
/// <para>The same failure has to be answerable on four surfaces: an HTTP response, a Hangfire job
/// outcome, a run's persisted error, and a tool result handed back to an agent. Only one of those is
/// HTTP, so a taxonomy expressed in status codes can only ever serve one consumer and leaves the
/// other three to invent their own — which is exactly what happened: a rate limit is a retryable 429
/// on the API and a permanent job failure in the background, from the same exception.</para>
///
/// <para>Each member answers one question: what should a caller DO about it. That question has the
/// same answer on every surface, which is what makes it the thing worth carrying.</para>
/// </summary>
public enum FailureKind
{
    /// <summary>The request was malformed or self-contradictory. Retrying it unchanged cannot help.</summary>
    Invalid,

    /// <summary>No usable identity was presented. Sign in and retry.</summary>
    Unauthenticated,

    /// <summary>Identity is known and insufficient. Retrying as the same principal cannot help.</summary>
    Forbidden,

    /// <summary>The addressed thing does not exist, or does not exist for this caller.</summary>
    NotFound,

    /// <summary>State moved underneath the caller — a lost CAS, a duplicate, an illegal transition. Re-read and decide again.</summary>
    Conflict,

    /// <summary>Well-formed and understood, but blocked by a rule. The caller must change something other than the request shape.</summary>
    Unprocessable,

    /// <summary>Something must be done FIRST, then this exact request will work. The remedy is nameable and is carried in the details.</summary>
    PreconditionRequired,

    /// <summary>A budget, quota, or concurrency limit is spent. The same request succeeds later, untouched.</summary>
    Exhausted,

    /// <summary>A dependency we do not own failed. Not the caller's fault and not ours; retrying may work.</summary>
    Unavailable,

    /// <summary>An invariant this system is supposed to hold did not hold. Never the caller's fault, never shown to them, always worth waking someone.</summary>
    Internal,
}
