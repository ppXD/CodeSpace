namespace CodeSpace.Messages.Contracts;

/// <summary>
/// Q's PROTOCOL axis (the v4.3 qualification split): can this entry's FAIL-CLOSE story be trusted — is the
/// conformance chain (stakes → receipts → gates → park) merely built, exercised in shadow, or sealed as
/// ENFORCEABLE (an internal Enforced canary may run it; production CleanSuccess requires at least this).
/// Orthogonal to <see cref="PerformanceQualification"/>: a protocol can be enforceable long before any
/// performance number stands.
/// </summary>
public enum ProtocolReadiness
{
    /// <summary>Built and iterating — the conformance chain is not yet trusted to fail closed.</summary>
    Open,

    /// <summary>The chain is exercised in shadow (assessments + would-be decisions recorded, nothing enforced).</summary>
    Shadow,

    /// <summary>The fail-close protocol is sealed — an Enforced cohort may run this entry.</summary>
    Enforceable,
}

/// <summary>
/// Q's PERFORMANCE axis: does a MEASURED claim stand — nothing measured, shadow evidence accumulating, or a
/// SEALED hidden-suite qualification (the only tier that backs a public capability number, minted as an
/// immutable <c>QualificationReceipt</c>). Revocation is registry-side and forward-only: it never retroactively
/// changes what an already-terminal run's semantics were.
/// </summary>
public enum PerformanceQualification
{
    /// <summary>No measured claim stands.</summary>
    Unmeasured,

    /// <summary>Shadow evidence accumulating toward a measurable claim.</summary>
    Shadow,

    /// <summary>Passed the sealed hidden-suite qualification — the only tier that backs a stated capability number.</summary>
    Sealed,
}

/// <summary>One registered capability: WHAT kind of deliverable the system can be asked for, and where its PROTOCOL readiness stands. The registry of these is the closed vocabulary — an ask outside it is honestly <c>Unsupported</c>, never a silent attempt (Lock Clause 4). Deliberately no performance column: measured standing resolves from the qualification-receipt ledger (Q4's claim gate), never from a committed constant.</summary>
public sealed record CapabilityDescriptor
{
    public required string Key { get; init; }

    public required ProtocolReadiness Readiness { get; init; }
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
