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

    /// <summary>Every contribution that did NOT apply, in its own words — "{label}: {reason}" (or the bare label when the outcome carried no reason), in outcome order, deduped. Names WHICH agent/branch/subtask failed and why, beside the aggregated <see cref="ConflictedFiles"/>/<see cref="PreservedBranches"/> above. Empty when clean/skipped or the outcomes carried no label.</summary>
    public IReadOnlyList<string> FailingContributions { get; init; } = Array.Empty<string>();

    /// <summary>The integrator's one-line reason for a non-clean status (e.g. "a contribution conflicted while integrating"), or null when clean.</summary>
    public string? Reason { get; init; }

    /// <summary>The single reviewable branch the integrator produced on a CLEAN integration (null when conflicted / skipped / failed) — the head a downstream open_pr targets.</summary>
    public string? IntegratedBranch { get; init; }

    /// <summary>True when the integration conflicted — the signal that gates the resolver loop. Case-insensitive against <see cref="Status"/>.</summary>
    public bool IsConflicted => string.Equals(Status, "Conflicted", StringComparison.OrdinalIgnoreCase);

    // ── The reason contract: the executor's own mint-side words, and the CAUSE tag a gate scopes a blocker by ──

    /// <summary>The publish-guard block's pinned reason PREFIX (the winning guard's own words follow it). Durable tape bytes AND the executor's mint literal — read from here on both sides so a reword can never silently reclassify an in-flight parked run's blocker. Pinned (Rule 8).</summary>
    public const string PolicyBlockedPrefix = "publish policy: ";

    /// <summary>The pinned reason for a repository that resolved to no clone target. Mint literal + classifier key, shared for the same reason as <see cref="PolicyBlockedPrefix"/>.</summary>
    public const string UnresolvedTargetReason = "the repository could not be resolved to a clone target";

    /// <summary>The single-repo pinned reason for a set with nothing integrable in it.</summary>
    public const string NoBaseRevisionReason = "no agent recorded a base revision (an analysis-only run has nothing to integrate)";

    /// <summary>The per-repo (multi-repo axis) pinned reason for a repository nothing integrable touched — the same CAUSE as <see cref="NoBaseRevisionReason"/>, worded for one axis.</summary>
    public const string NoRepositoryChangeReason = "no agent changed this repository with a recorded base revision";

    /// <summary>Cause tag: the repository's publish policy forbade the integration.</summary>
    public const string PolicyCause = "policy";

    /// <summary>Cause tag: the repository itself could not be resolved to something clonable.</summary>
    public const string UnresolvedRepositoryCause = "unresolved-repository";

    /// <summary>Cause tag: the set carried nothing that could be integrated.</summary>
    public const string NothingIntegrableCause = "nothing-integrable";

    /// <summary>Cause tag: the reason names no cause this contract knows — a git error string, or a multi-repo aggregate's count sentence. STABLE by construction (it is a constant, never the prose), so it scopes a blocker without re-asking a question whose wording drifted.</summary>
    public const string UnclassifiedCause = "unclassified";

    /// <summary>
    /// WHICH cause a non-clean integration's <see cref="Reason"/> describes — the tag a stop gate's blocker carries
    /// BESIDE <see cref="Status"/>. Four distinct causes all record the status "Skipped"
    /// (<c>RealSupervisorActionExecutor.IntegrateMergedAsync</c>), so the status alone lets an answer about one of
    /// them release the card a different one raises. Classified off the pinned mint literals above rather than the
    /// prose, which carries provider error text and per-attempt counts a comparison must never key on.
    /// </summary>
    public static string CauseTag(string? reason) =>
        reason is null ? UnclassifiedCause
        : reason.StartsWith(PolicyBlockedPrefix, StringComparison.Ordinal) ? PolicyCause
        : reason == UnresolvedTargetReason ? UnresolvedRepositoryCause
        : reason == NoBaseRevisionReason || reason == NoRepositoryChangeReason ? NothingIntegrableCause
        : UnclassifiedCause;
}
