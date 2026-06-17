namespace CodeSpace.Messages.Agents;

/// <summary>
/// The structured, fail-safe report of an on-disk branch integration (SOTA #3). The reviewable artifact the
/// integrator always produces — a pure data noun (Rule 18.1). Either the K contributions integrated cleanly into
/// one branch (<see cref="IntegrationStatus.Clean"/> + <see cref="IntegratedBranch"/>) OR the integration aborted
/// fail-safe (<see cref="IntegrationStatus.Conflicted"/>, no branch, the per-contribution detail naming what could
/// not be applied) and the original K agent branches/patches remain intact for human review.
///
/// <para><b>The honesty invariant:</b> a set containing ANY <see cref="ContributionDisposition.Unintegrable"/>
/// contribution can NEVER be <see cref="IntegrationStatus.Clean"/> — a dropped agent is loudly named, never hidden
/// inside a green result. Enforced by <see cref="Build"/>, the single constructor every caller uses.</para>
/// </summary>
public sealed record IntegrationResult
{
    /// <summary>The whole-set outcome. <see cref="IntegrationStatus.Clean"/> only when EVERY contribution applied and a branch was pushed.</summary>
    public required IntegrationStatus Status { get; init; }

    /// <summary>The run-id-derived branch the clean integration published. Null on any non-clean outcome. The publish is fail-safe against clobbering foreign work: a plain push when the branch is absent (git's own non-fast-forward rejection catches a concurrent create), and when the branch already exists it is reused as a no-op ONLY if its tree byte-equals ours — a differing tree is refused as "advanced", never overwritten.</summary>
    public string? IntegratedBranch { get; init; }

    /// <summary>How many contributions were applied into the integrated branch (equals the contribution count on <see cref="IntegrationStatus.Clean"/>; 0 on a fail-safe abort).</summary>
    public int AppliedCount { get; init; }

    /// <summary>A human-readable note on the whole-set outcome (e.g. the abort reason: "contributions span multiple repositories", "remote integration branch advanced"). Null when nothing extra to say.</summary>
    public string? Reason { get; init; }

    /// <summary>Per-contribution disposition (in request order) — what happened to each agent's work, so a dropped contribution is always traceable.</summary>
    public IReadOnlyList<ContributionOutcome> Outcomes { get; init; } = Array.Empty<ContributionOutcome>();

    /// <summary>
    /// The single guarded constructor. Computes <see cref="AppliedCount"/> and ENFORCES the honesty invariant:
    /// <see cref="IntegrationStatus.Clean"/> means EVERY contribution was applied. If ANY contribution was not
    /// <see cref="ContributionDisposition.Applied"/> (conflicted-with-fallback OR unintegrable), a proposed Clean is
    /// coerced to <see cref="IntegrationStatus.Conflicted"/> with the branch cleared — a Clean result that hides a
    /// not-integrated contribution is a contradiction the type refuses to emit.
    /// </summary>
    public static IntegrationResult Build(IntegrationStatus proposedStatus, string? integratedBranch, IReadOnlyList<ContributionOutcome> outcomes, string? reason = null)
    {
        var anyNotApplied = outcomes.Any(o => o.Disposition != ContributionDisposition.Applied);

        var status = proposedStatus == IntegrationStatus.Clean && anyNotApplied ? IntegrationStatus.Conflicted : proposedStatus;

        var clean = status == IntegrationStatus.Clean;

        return new IntegrationResult
        {
            Status = status,
            IntegratedBranch = clean ? integratedBranch : null,
            AppliedCount = clean ? outcomes.Count(o => o.Disposition == ContributionDisposition.Applied) : 0,
            Reason = reason,
            Outcomes = outcomes,
        };
    }
}

/// <summary>What happened to ONE contribution during integration. Distinguishes a clean apply, a conflict that fell back to a reviewable branch, and a contribution that could not be integrated at all.</summary>
public sealed record ContributionOutcome
{
    /// <summary>The contribution's label (matches <see cref="BranchContribution.Label"/>).</summary>
    public required string Label { get; init; }

    /// <summary>The disposition (the honesty axis — see <see cref="ContributionDisposition"/>).</summary>
    public required ContributionDisposition Disposition { get; init; }

    /// <summary>Files this contribution conflicted on (textual 3-way conflict), when <see cref="Disposition"/> is <see cref="ContributionDisposition.Conflicted"/>. Empty otherwise.</summary>
    public IReadOnlyList<string> ConflictedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The agent's pushed branch preserved for human review when this contribution could not be integrated cleanly. Null when the agent pushed none (then it is <see cref="ContributionDisposition.Unintegrable"/> — no fallback).</summary>
    public string? FallbackBranch { get; init; }

    /// <summary>Why this contribution was not applied (e.g. "base SHA mismatch", "diff exceeded inline cap", "no patch and no branch"). Null when applied cleanly.</summary>
    public string? Reason { get; init; }
}

/// <summary>The whole-set integration outcome.</summary>
public enum IntegrationStatus
{
    /// <summary>Every contribution applied; one integrated branch was pushed.</summary>
    Clean,

    /// <summary>The integration aborted fail-safe (a textual conflict, a base mismatch, a multi-repo set, a diverged remote branch refused as "advanced", or an unintegrable contribution). No branch published; the clone tree was reset to base; the K agent branches/patches remain intact.</summary>
    Conflicted,

    /// <summary>FORWARD-COMPAT (not produced by the default all-or-nothing policy): some contributions applied, others were reported. Reserved for a future partial-integrate mode.</summary>
    Partial,

    /// <summary>No contributions to integrate (or all empty/no-op).</summary>
    Empty,
}

/// <summary>What happened to one contribution — the honesty axis that makes a silently-dropped agent impossible.</summary>
public enum ContributionDisposition
{
    /// <summary>Applied cleanly into the integrated branch.</summary>
    Applied,

    /// <summary>Conflicted textually; the whole set aborted, but this agent's work is preserved on <see cref="ContributionOutcome.FallbackBranch"/> for human review.</summary>
    Conflicted,

    /// <summary>Could NOT be integrated AND has no reviewable fallback branch (a missing/empty/truncated patch with no pushed branch, a base mismatch, or a multi-repo refusal). A set with ANY of these is never <see cref="IntegrationStatus.Clean"/> — the dropped agent is loudly named.</summary>
    Unintegrable,
}
