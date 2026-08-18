namespace CodeSpace.Messages.Contracts;

/// <summary>The completion protocol's ten-stage chain — the axes a mode's conformance is declared over (P4 / Lock Clause 4).</summary>
public enum CompletionStage
{
    Contract,
    Plan,
    Execute,
    Integrate,
    Verify,
    Capture,
    Deliver,
    Handoff,
    Assess,
    Terminal,
}

/// <summary>
/// A stage's requiredness inside one mode's profile. AUTHORED authority only: <c>Required</c> means the mode's
/// runs must produce that stage's evidence; the two authorized-NA variants mean an AUTHORITY declared the stage
/// not owed for this mode (mirroring <see cref="Requiredness"/>'s discipline — a model proposal can never set a
/// stage N/A; there is deliberately no such member).
/// </summary>
public enum StageRequiredness
{
    Required,
    OperatorAuthorizedNotApplicable,
    ServerPolicyAuthorizedNotApplicable,
}

/// <summary>
/// P4 (Lock Clause 4): one operating mode's declared conformance shape — which of the ten stages its runs owe,
/// and the mode's protocol standing. A COMMITTED noun: profiles live in the registry's source, changed
/// by PR, never by deployment toggle. An unregistered mode resolves null and the terminal authority fails CLOSED
/// (Unsupported park) — a run whose operating shape has no declared conformance story must never terminalize an
/// Enforced Success, exactly as an unregistered capability must not.
/// </summary>
public sealed record ModeProfile
{
    /// <summary>The mode key — an open string matching <c>RunModeClassifier</c>'s derivation (e.g. <c>"supervisor"</c>, <c>"single-agent"</c>, <c>"plan-map"</c>).</summary>
    public required string Mode { get; init; }

    /// <summary>Every stage's declared requiredness — total over <see cref="CompletionStage"/>; the registry validates totality at construction so a new stage cannot be silently unmapped.</summary>
    public required IReadOnlyDictionary<CompletionStage, StageRequiredness> Stages { get; init; }

    /// <summary>The mode's PROTOCOL axis — can its fail-close conformance chain be trusted (Enforced graduation is argued per (mode, capability) on accumulated evidence). Deliberately the ONLY qualification column here: measured performance is never a committed constant — it resolves from the qualification-receipt ledger (Q4's claim gate).</summary>
    public required ProtocolReadiness Readiness { get; init; }
}
