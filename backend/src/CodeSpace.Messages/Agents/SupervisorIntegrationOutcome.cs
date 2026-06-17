namespace CodeSpace.Messages.Agents;

/// <summary>
/// The COMPACT, decider-visible result of a supervisor <c>merge</c>'s on-disk branch INTEGRATION (SOTA #3 /
/// the resolver loop #379). Projected from the <c>integration</c> block a merge decision records — minus the
/// noise the decider never acts on (per-contribution dispositions, agent-run GUIDs) — so the decider PERCEIVES
/// "the agents' work conflicted on these files" and can choose to attempt resolution or stop. A pure data noun
/// (Rule 18.1): a read of immutable post-merge state, replay-deterministic.
///
/// <para>The single conflict-legibility signal the resolver loop hangs off: when <see cref="IsConflicted"/>, the
/// agents' work is never clobbered (the integrator's honesty invariant) and the CONFLICTING contributions' branches
/// are named in <see cref="PreservedBranches"/>. The resolver's COMPLETE re-merge input (every agent's branch, not
/// just the conflicting ones) is assembled separately from the spawn's agent results; this block's job is conflict
/// DETECTION — what conflicted, and where the conflicting work was kept. Built by <c>SupervisorOutcome.ReadIntegration</c>.</para>
/// </summary>
public sealed record SupervisorIntegrationOutcome
{
    /// <summary>The integration status name as the integrator reported it: "Clean" (one reviewable branch), "Conflicted" (the K branches couldn't auto-combine), "Skipped" (nothing to integrate), or "Failed" (a git infrastructure error). Never null — a present integration block always carries a status.</summary>
    public required string Status { get; init; }

    /// <summary>The repo-relative paths that conflicted while integrating (empty unless <see cref="IsConflicted"/>). The resolver agent's instruction names these so it knows exactly what to reconcile.</summary>
    public IReadOnlyList<string> ConflictedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The branches of the CONFLICTING contributions the integrator preserved for review (its <c>fallbackBranch</c>es — set only on contributions that could NOT be cleanly applied; a cleanly-applied agent's branch is not surfaced here). Empty when the integration was clean / skipped / failed-without-branches.</summary>
    public IReadOnlyList<string> PreservedBranches { get; init; } = Array.Empty<string>();

    /// <summary>The integrator's one-line reason for a non-clean status (e.g. "a contribution conflicted while integrating"), or null when clean.</summary>
    public string? Reason { get; init; }

    /// <summary>The single reviewable branch the integrator produced on a CLEAN integration (null when conflicted / skipped / failed) — the head a downstream open_pr targets.</summary>
    public string? IntegratedBranch { get; init; }

    /// <summary>True when the integration conflicted — the signal that gates the resolver loop. Case-insensitive against <see cref="Status"/>.</summary>
    public bool IsConflicted => string.Equals(Status, "Conflicted", StringComparison.OrdinalIgnoreCase);
}
