namespace CodeSpace.Messages.Agents;

/// <summary>
/// What <c>ISupervisorDecisionLog.TryClaimAsync</c> needs to INSERT one Pending decision row (a data noun,
/// Rule 18.1 — primitives only, never the Core entity). Grouped rather than passed loose: the claim already
/// carried seven arguments before the lesson arm, well past the parameter cap, and the two frozen hash-derived
/// strings sat next to each other where a transposition would be silent.
/// </summary>
public sealed record SupervisorDecisionClaimRequest
{
    /// <summary>The supervisor run this decision belongs to (a soft link — the ledger outlives its run row).</summary>
    public required Guid SupervisorRunId { get; init; }

    /// <summary>Tenancy on every row.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The decision verb — a <c>SupervisorDecisionKinds</c> value.</summary>
    public required string DecisionKind { get; init; }

    /// <summary>The SERVER-derived at-most-once handle (<c>DeriveIdempotencyKey</c>) — never read from the model.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Lower-case hex SHA-256 of the canonical payload — the audit column the key already binds.</summary>
    public required string InputHash { get; init; }

    /// <summary>The emitted decision, canonicalized by the caller. Frozen at insert.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>Mirrors the run's fence epoch at claim time — recorded for audit/forensics only, never a CAS guard.</summary>
    public required long FenceEpoch { get; init; }

    /// <summary>
    /// D2: the lesson-experiment arm the turn's prompt was built under — a <c>LessonArms</c> value. Stamped so a
    /// scorecard can slice supervisor runs by arm, and so later turns read the run's assignment back off the tape
    /// instead of re-rolling it. Null/blank ⇒ the column stays NULL (a caller outside the experiment).
    /// </summary>
    public string? LessonArm { get; init; }
}
