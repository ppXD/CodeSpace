namespace CodeSpace.Messages.Health;

/// <summary>What the refresh-lock self-test found out about the Postgres connections this process opens.</summary>
public enum RefreshLockOutcome
{
    /// <summary>A second session could not take the advisory lock the first one held, and the first released it as its own: no sign that connections are multiplexed. A finding of absence, not a proof — a pooler that picks a server session per statement can slip past it.</summary>
    Exclusive,

    /// <summary>The connections this process opens do not map one-to-one onto server sessions — a transaction-mode pooler, RDS Proxy, or a read-write splitting proxy. Either a second session took the advisory lock the first one held, or the first, unlocking the key it had just taken, was told it did not hold it.</summary>
    NotExclusive,

    /// <summary>The test could not run to its end (no connection, a timeout). Nothing is known about the lock either way.</summary>
    Inconclusive,
}

/// <summary>The result of one refresh-lock self-test, with the sentence an operator reads and when it was observed.</summary>
public sealed record RefreshLockVerdict(RefreshLockOutcome Outcome, string Detail, DateTimeOffset ObservedAt);
