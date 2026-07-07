using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// The PLATFORM'S policy over a critic verdict's severities — the pyramid discipline made explicit: the MODEL grades
/// each issue (what's wrong + how bad, <see cref="CriticSeverity"/>), the PLATFORM decides halt-vs-proceed from a
/// deterministic rule over those grades. This is the fix for adversarial review that blocks on every nitpick — a
/// <see cref="CriticSeverity.Minor"/> issue no longer carries the same halting power as a fatal one.
///
/// <para>Pure + policy-central: every gate site (<c>CriticSupervisorDeciderDecorator</c>, <c>AgentRunExecutor</c>,
/// <c>CriticPlannerDecorator</c>) reads the SAME derived <see cref="CriticVerdict.Approved"/> (computed here at
/// projection time from the issues), so the calibration is one rule, one place, generic across every critic — never a
/// per-caller reinterpretation. Changing the halt rule (e.g. a strict mode that also halts on Major) is a one-line
/// edit here, unit-pinned.</para>
/// </summary>
public static class CriticGatePolicy
{
    /// <summary>
    /// Whether a gate APPROVES the artifact — SEVERITY-AUTHORITATIVE: a gate halts iff at least one issue is a
    /// <see cref="CriticSeverity.Blocker"/>. So a Minor-or-Major-only disapproval no longer halts (the calibration
    /// fix — "correctly addresses the root cause but has a material [major] flaw" proceeds with the flaw surfaced),
    /// and a Blocker the model under-called with <c>approved:true</c> still halts (the safety catch). No issues ⇒
    /// approved. The oracle/rubric layer is the deterministic gate for correctness; the critic is advisory calibration.
    /// </summary>
    public static bool Approves(IReadOnlyList<CriticIssue> issues) =>
        !issues.Any(i => i.Severity == CriticSeverity.Blocker);

    /// <summary>
    /// Whether an IMPROVE verdict WARRANTS a bounded revision round — true when it carries at least one Blocker or
    /// Major issue (a material problem worth another pass). A critique whose issues are ALL Minor is nitpick-only:
    /// revising against it burns a round for no material gain, so it is suppressed (treated like an approval). A
    /// critique with NO structured issues keeps its revision — a genuine free-text critique whose severity is unknown
    /// must not be silently dropped (fail toward doing the review, the safe direction).
    /// </summary>
    public static bool WarrantsRevision(IReadOnlyList<CriticIssue> issues) =>
        issues.Count == 0 || issues.Any(i => i.Severity is CriticSeverity.Blocker or CriticSeverity.Major);
}
