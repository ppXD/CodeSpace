namespace CodeSpace.Messages.Decisions;

/// <summary>
/// One parked <c>decision.request</c> ledger row the deadline reaper (Decision substrate D5b) just answered with its
/// configured default — the minimal handle the orchestrating expiry service needs for its two best-effort follow-ups:
/// wake any blocked handler waiter (<see cref="LedgerId"/>) so it reads the now-<c>Succeeded</c> terminal + the default
/// answer, and mirror the decision card to timed-out (<see cref="ApprovalMessageId"/>, null when no card was posted).
/// A data noun (Rule 18.1) — primitives only, never the Core entity. The reaper sweeps team-agnostically; <see cref="TeamId"/>
/// is carried for log/forensics correlation. The <c>ExpiredToolApproval</c> analogue for the decision grain.
/// </summary>
public sealed record TimedOutDecision
{
    public required Guid LedgerId { get; init; }

    /// <summary>The owning team — carried for log correlation; the sweep itself is team-agnostic.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The decision-card message to mirror to timed-out. Null when the row timed out before any card was recorded.</summary>
    public Guid? ApprovalMessageId { get; init; }
}
