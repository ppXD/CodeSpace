using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// P3b-4: the ONE pure mapping from the five-dimensional assessment (+ the handoff-reachability and
/// capability-support facts) to the sealed six-state <see cref="TerminalDecision"/>. INACTIVE by design: shadow
/// records what the decision WOULD be; nothing here (or downstream of here) mutates a run's terminal until P2b
/// activates per qualified cohort (Lock Clause 1). The transition table is PINNED test-by-test; the states are
/// sealed, and any arm change is a P2b-visible decision, never an invisible refactor.
/// </summary>
public static class TerminalDecider
{
    /// <summary>Exactly one state may enter VDS — the predicate the north-star counts (Lock Clause 5).</summary>
    public static bool IsVdsEligible(TerminalDecision decision) => decision == TerminalDecision.CleanSuccess;

    public static TerminalDecision Decide(CompletionAssessment assessment, bool handoffReachable, bool capabilitySupported = true)
    {
        if (!capabilitySupported) return TerminalDecision.Unsupported;

        // Legacy tape is never re-derived into a terminal claim — a human reads it, the decider does not.
        if (assessment.Basis == CompletionBasis.LegacyUnknown) return TerminalDecision.NeedsReview;

        // Disorderly execution can't be honestly decided; a forced stop or cancellation is an honest non-success.
        if (assessment.Execution == ExecutionDisposition.Crashed) return TerminalDecision.NeedsReview;
        if (assessment.Execution is ExecutionDisposition.ForcedStop or ExecutionDisposition.Cancelled) return TerminalDecision.HonestFailure;

        switch (assessment.Outcome)
        {
            case OutcomeDisposition.Unknown: return TerminalDecision.NeedsReview;
            case OutcomeDisposition.Abstained: return TerminalDecision.NeedsClarification;
            case OutcomeDisposition.Unsolved: return TerminalDecision.HonestFailure;
        }

        // Outcome == Solved from here. Each conjunct of the clean-success predicate gets its honest fallback.
        if (assessment.Verification is not (VerificationDisposition.Passed or VerificationDisposition.NotApplicable))
            return TerminalDecision.NeedsReview;   // Waived/HumanReviewRequired/InfraUnknown/Failed/Unknown — never VDS (Lock Clause 5)

        if (assessment.Artifact == ArtifactDisposition.CaptureFailed) return TerminalDecision.HonestFailure;
        if (assessment.Artifact == ArtifactDisposition.Unknown) return TerminalDecision.NeedsReview;

        if (assessment.Delivery is DeliveryDisposition.PolicyBlocked or DeliveryDisposition.WaivedByPolicy) return TerminalDecision.Park;
        if (assessment.Delivery == DeliveryDisposition.Unknown) return TerminalDecision.NeedsReview;

        if (!handoffReachable) return TerminalDecision.Park;

        return TerminalDecision.CleanSuccess;
    }
}
