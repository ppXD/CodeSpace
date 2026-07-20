namespace CodeSpace.Messages.Contracts;

/// <summary>
/// Q's three-tier evaluation governance, applied per capability (v4.2-FINAL): where this capability's claims
/// currently stand. Only <see cref="SealedQualification"/> backs a public capability NUMBER; the other tiers are
/// honest "still developing/measuring" states. Enforcement cohort selection READS this — it never bypasses it.
/// </summary>
public enum QualificationStatus
{
    /// <summary>Built and iterating — no measured claim stands.</summary>
    OpenDevelopment,

    /// <summary>Shadow evidence accumulating (assessments + would-be terminal decisions recorded, nothing enforced).</summary>
    ShadowEvaluation,

    /// <summary>Passed the sealed qualification protocol — the only tier that backs a stated capability number.</summary>
    SealedQualification,
}

/// <summary>One registered capability: WHAT kind of deliverable the system can be asked for, and where its qualification stands. The registry of these is the closed vocabulary — an ask outside it is honestly <c>Unsupported</c>, never a silent attempt (Lock Clause 4).</summary>
public sealed record CapabilityDescriptor
{
    public required string Key { get; init; }

    public required QualificationStatus Qualification { get; init; }
}

/// <summary>The registered capability KEYS — the wire vocabulary (a new capability = a new const + a registry row + its verifier, per the Rule-8 ritual).</summary>
public static class CapabilityKeys
{
    /// <summary>Work delivered as a pushed git branch (the PR-able surface).</summary>
    public const string GitBranch = "git-branch";

    /// <summary>Work captured as a recorded patch artifact (no remote arrival owed).</summary>
    public const string GitPatch = "git-patch";

    /// <summary>Read-only work whose deliverable is the answer itself (analysis, review, report text).</summary>
    public const string InlineAnswer = "inline-answer";
}
