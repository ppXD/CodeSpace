namespace CodeSpace.Messages.Agents;

/// <summary>
/// One <c>AwaitingApproval</c> ledger row the deadline reaper (item D3) just flipped to <c>Expired</c> — the minimal
/// handle the orchestrating expiry service needs for its two best-effort follow-ups: wake any blocked handler waiter
/// (<see cref="LedgerId"/>) and mirror the approval card to timed-out (<see cref="ApprovalMessageId"/>, null when no
/// card was ever posted). A data noun (Rule 18.1) — primitives only, never the Core entity. <see cref="TeamId"/> is
/// carried for log/forensics correlation; the reaper sweeps team-agnostically (an internal job with no actor).
/// </summary>
public sealed record ExpiredToolApproval
{
    public required Guid LedgerId { get; init; }

    /// <summary>The owning team — carried for log correlation; the sweep itself is team-agnostic.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The approval-card message to mirror to timed-out. Null when the row expired before any card was recorded.</summary>
    public Guid? ApprovalMessageId { get; init; }
}
