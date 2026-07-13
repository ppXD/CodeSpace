using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// THE one completion reducer (F0 / v4.1-F): a pure, deterministic fold from requirement/receipt envelopes + run
/// facts to the five-dimensional <see cref="CompletionAssessment"/>. Same contract + same facts ⇒ same assessment,
/// on every surface — this is the single verdict every consumer (scorecard, gates, Room, journal) converges onto,
/// replacing today's three independent ladders (manifest→status, stop-grade→label→status, payload-only
/// classification). Kind-AGNOSTIC by construction: it routes on <see cref="ContractKinds"/> registry keys and reads
/// dispositions/refs only — it never opens a kind's payload, so Git/PR/artifact semantics cannot leak into the
/// kernel. No I/O, no clock, no logging — the composer owns fact acquisition; this owns only the fold.
/// </summary>
public static class CompletionReducer
{
    /// <summary>Reduce a CONTRACT-ERA run's envelopes + facts to its assessment. Callers gate on <see cref="CompletionCutover"/> first — a pre-cutover run takes <see cref="ReduceLegacy"/> instead, never this.</summary>
    public static CompletionAssessment Reduce(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<ReceiptEnvelope> receipts, CompletionRunFacts facts)
    {
        var execution = ClassifyExecution(facts);

        // "A model may propose, never authorize" — ENFORCED IN THE KERNEL: a receipt whose Authority is
        // ModelProposal (a self-report) never participates in any objective dimension. It rides the tape for
        // renderers and contradiction detection, but it cannot satisfy a requirement, mint an exemption, or move
        // a metric; a required requirement answered ONLY by a self-report reads Unknown, exactly as unanswered.
        var authorized = receipts.Where(r => r.Authority != ContractAuthority.ModelProposal).ToList();

        var verification = AggregateVerification(requirements, authorized);

        return new CompletionAssessment
        {
            Basis = CompletionBasis.ContractDerived,
            Execution = execution,
            ForcedStopReason = execution == ExecutionDisposition.ForcedStop ? facts.ForcedStopReason : null,
            Outcome = ClassifyOutcome(verification, execution, facts),
            Verification = verification,
            Artifact = ClassifyArtifact(requirements, authorized),
            Delivery = ClassifyDelivery(requirements, authorized),
        };
    }

    /// <summary>
    /// The LegacyUnknown projection (CUTOVER): a run created before <see cref="CompletionCutover"/> derives ONLY
    /// <see cref="CompletionAssessment.Execution"/> (its terminal status is a durable fact); every contract
    /// dimension stays <c>Unknown</c>. Old facts are never re-derived into contract truth — recomputation over a
    /// tape that predates the contract regime would manufacture false precision.
    /// </summary>
    public static CompletionAssessment ReduceLegacy(CompletionRunFacts facts) => new()
    {
        Basis = CompletionBasis.LegacyUnknown,
        Execution = ClassifyExecution(facts),
        ForcedStopReason = ClassifyExecution(facts) == ExecutionDisposition.ForcedStop ? facts.ForcedStopReason : null,
        Outcome = OutcomeDisposition.Unknown,
        Verification = VerificationDisposition.Unknown,
        Artifact = ArtifactDisposition.Unknown,
        Delivery = DeliveryDisposition.Unknown,
    };

    /// <summary>
    /// The terminal-CAS precondition (F0): an undecidable run must not terminalize as a clean completion. False
    /// EXACTLY when <see cref="ExecutionDisposition.Completed"/> meets <see cref="OutcomeDisposition.Unknown"/> —
    /// a run claiming an orderly finish whose objective truth cannot be stated must park for adjudication instead.
    /// Every honest end (<c>Crashed</c>/<c>Cancelled</c>/<c>ForcedStop</c>) always terminalizes — park-don't-die
    /// never blocks recording a death — and a decided or human-abstained outcome always terminalizes.
    /// </summary>
    public static bool IsTerminalizable(CompletionAssessment assessment) =>
        assessment.Execution != ExecutionDisposition.Completed || assessment.Outcome != OutcomeDisposition.Unknown;

    /// <summary>
    /// HOW the run ended, independent of whether it succeeded (a Failure that ran its course is still
    /// <see cref="ExecutionDisposition.Completed"/> — the failing is Outcome/Verification business). Precedence:
    /// a human cancel wins over everything; a recorded forced-stop reason wins over tape shape; a missing orderly
    /// terminal is an engine death; everything else ran to completion.
    /// </summary>
    private static ExecutionDisposition ClassifyExecution(CompletionRunFacts facts)
    {
        if (facts.TerminalStatus == WorkflowRunStatus.Cancelled) return ExecutionDisposition.Cancelled;

        if (!string.IsNullOrEmpty(facts.ForcedStopReason)) return ExecutionDisposition.ForcedStop;

        if (!facts.HadOrderlyTerminal) return ExecutionDisposition.Crashed;

        return ExecutionDisposition.Completed;
    }

    /// <summary>
    /// The acceptance-kind fold. Zero acceptance requirements → <see cref="VerificationDisposition.NotApplicable"/>
    /// (nothing was ever owed). Otherwise the shared per-requirement fold (<see cref="FoldKind"/>) under the
    /// worst-deficit-first severity order, with ONE acceptance-specific normalization: a receipt claiming
    /// <see cref="VerificationDisposition.NotApplicable"/> reads as <c>Unknown</c> — the vacuous-pass
    /// reclassification (today a vacuous pass writes <c>Passed=true</c>; letting an inapplicability claim reach
    /// the status-fallback arm would silently pre-decide it) is RESERVED for its own test-pinned PR, and until
    /// then an inapplicability claim cannot move truth in either direction.
    /// </summary>
    private static VerificationDisposition AggregateVerification(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<ReceiptEnvelope> receipts) =>
        FoldKind(requirements, receipts, ContractKinds.Acceptance, (_, sanitized) => sanitized == VerificationDisposition.NotApplicable ? VerificationDisposition.Unknown : sanitized)
            ?? VerificationDisposition.NotApplicable;

    /// <summary>
    /// The shared per-requirement fold for one <see cref="ContractKinds"/> key. Every requirement of the kind is
    /// matched to ITS OWN receipts by <see cref="RequirementEnvelope.RequirementRef"/> — an orphan receipt whose
    /// ref matches no requirement never participates (a receipt cannot mint an obligation, let alone satisfy one).
    /// A REQUIRED requirement with no receipt reads <c>Unknown</c>; a missing OPTIONAL receipt is ignored
    /// (Requiredness.Optional never blocks) while a PRESENT optional receipt participates (it lowers the recorded
    /// dimension). A requirement declaring <see cref="RequirementEnvelope.ExpectedCardinality"/> N that received
    /// fewer than N receipts is UNDER-DELIVERED — an <c>Unknown</c> joins its fold, so 1-of-3 verified targets can
    /// never read as fully verified. Returns null when the kind has no requirements, or only unanswered optional
    /// ones (nothing owed / nothing recorded) — the caller names that state per dimension.
    /// </summary>
    private static VerificationDisposition? FoldKind(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<ReceiptEnvelope> receipts, string kind, Func<ReceiptEnvelope, VerificationDisposition, VerificationDisposition>? normalize = null)
    {
        var kindRequirements = requirements.Where(r => r.Kind == kind).ToList();

        if (kindRequirements.Count == 0) return null;

        var receiptsByRef = receipts.Where(r => r.Kind == kind)
            .GroupBy(r => r.RequirementRef, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var observed = new List<VerificationDisposition>();

        foreach (var requirement in kindRequirements)
        {
            if (receiptsByRef.TryGetValue(requirement.RequirementRef, out var matched))
                observed.Add(FoldRequirement(requirement, matched, normalize));
            else if (requirement.Requiredness == Requiredness.Required)
                observed.Add(VerificationDisposition.Unknown);
        }

        if (observed.Count == 0) return null;   // only optional requirements, none answered — nothing recorded to lower the dim

        return WorstOf(observed);
    }

    /// <summary>
    /// One requirement's own fold: its receipts — each SANITIZED first (a disposition outside the known severity
    /// order, i.e. a corrupt tape value, reads <c>Unknown</c> per element so it can never be outranked and masked
    /// by a sibling's Passed), then kind-normalized — worst-first, plus an injected <c>Unknown</c> when the
    /// receipt count is short of the declared <see cref="RequirementEnvelope.ExpectedCardinality"/>
    /// (under-delivery is a truth hole, not a pass; a corrupt declared cardinality below 1 clamps to 1 — the
    /// kernel cannot reconstruct the authored value, and mint-time validation owns authorship integrity, as it
    /// owns <see cref="RequirementEnvelope.RequirementRef"/> uniqueness within a kind).
    /// </summary>
    private static VerificationDisposition FoldRequirement(RequirementEnvelope requirement, IReadOnlyList<ReceiptEnvelope> matched, Func<ReceiptEnvelope, VerificationDisposition, VerificationDisposition>? normalize)
    {
        var dispositions = matched.Select(r =>
        {
            var sanitized = SeverityOrder.Contains(r.Disposition) ? r.Disposition : VerificationDisposition.Unknown;
            return normalize?.Invoke(r, sanitized) ?? sanitized;
        }).ToList();

        if (matched.Count < Math.Max(requirement.ExpectedCardinality ?? 1, 1)) dispositions.Add(VerificationDisposition.Unknown);

        return WorstOf(dispositions);
    }

    private static readonly VerificationDisposition[] SeverityOrder =
    {
        VerificationDisposition.Failed,
        VerificationDisposition.Unknown,
        VerificationDisposition.InfraUnknown,
        VerificationDisposition.HumanReviewRequired,
        VerificationDisposition.Waived,
        VerificationDisposition.Passed,
        VerificationDisposition.NotApplicable,
    };

    /// <summary>Worst-deficit-first over <see cref="SeverityOrder"/>; a disposition OUTSIDE the known order (a corrupt tape value) reads <c>Unknown</c> — the reducer degrades to "truth cannot be stated", it never throws.</summary>
    private static VerificationDisposition WorstOf(IReadOnlyList<VerificationDisposition> dispositions)
    {
        foreach (var candidate in SeverityOrder)
            if (dispositions.Contains(candidate)) return candidate;

        return VerificationDisposition.Unknown;
    }

    /// <summary>
    /// The objective-outcome dimension — the M1a four-state vocabulary at run grain. A verification verdict is
    /// authoritative when one exists: <c>Passed</c> → Solved, <c>Failed</c> → Unsolved, <c>Waived</c> → Abstained
    /// (a human authorized forgoing verification: no objective claim in EITHER direction — never Solved, so a
    /// waiver cannot move a solve-rate, and never Unsolved, so it cannot punish either), and every non-verdict
    /// (<c>InfraUnknown</c>/<c>HumanReviewRequired</c>/<c>Unknown</c>) → Unknown (truth cannot be stated).
    /// <see cref="VerificationDisposition.NotApplicable"/> (no oracle ever owed) falls back to the run's own honest
    /// end — Completed+Success → Solved, everything else → Unsolved — mirroring the scorecard's and
    /// <c>SupervisorEvalScorecard.ClassifyByRunStatus</c>'s shared precedent so the 2b consumer switch is
    /// metric-neutral for the no-contract population.
    /// </summary>
    private static OutcomeDisposition ClassifyOutcome(VerificationDisposition verification, ExecutionDisposition execution, CompletionRunFacts facts) => verification switch
    {
        VerificationDisposition.Passed => OutcomeDisposition.Solved,
        VerificationDisposition.Failed => OutcomeDisposition.Unsolved,
        VerificationDisposition.Waived => OutcomeDisposition.Abstained,
        VerificationDisposition.NotApplicable => execution == ExecutionDisposition.Completed && facts.TerminalStatus == WorkflowRunStatus.Success
            ? OutcomeDisposition.Solved
            : OutcomeDisposition.Unsolved,
        _ => OutcomeDisposition.Unknown,
    };

    /// <summary>
    /// The output-kind fold. No output requirement → <c>Unknown</c> (nothing was contracted — the reducer cannot
    /// claim "nothing expected" out of thin air). Receipts fold per requirement (<see cref="FoldKind"/>) with one
    /// output-specific normalization: content hashes are positive capture evidence, so a non-failing receipt
    /// carrying hashes reads <c>Passed</c>. The folded verdict maps: <c>Failed</c> → CaptureFailed; <c>Passed</c>
    /// → Captured; <c>NotApplicable</c> → NothingExpected (the AUTHORIZED exemption — the composer encodes an
    /// <see cref="OutputExpectation.NoOutputExpected"/> authority as a NotApplicable receipt, authority-checked at
    /// mint time, so the kernel stays kind-agnostic); everything else (incl. a Waived output, which has no
    /// defined semantics until the amend-acceptance arc) → Unknown.
    /// </summary>
    private static ArtifactDisposition ClassifyArtifact(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<ReceiptEnvelope> receipts)
    {
        // The hash upgrade rides the per-receipt hook so it can only ever lift the receipt it evidences — a
        // hole-detection Unknown (an unanswered sibling requirement, an under-delivered cardinality) survives
        // the fold and is never masked by another receipt's hashes. It lifts ONLY a verdict-less receipt
        // (Unknown): a Failed capture stays failed even with partial hashes, a Waived receipt is NEVER rewritten
        // toward Passed inside the kernel (the FATAL-1 boundary — its artifact semantics wait for the
        // amend-acceptance arc), an AUTHORIZED NotApplicable exemption is not overridden by an incidental hash, and a corrupt disposition (sanitized to Unknown) is never lifted — the raw value gates the upgrade.
        var folded = FoldKind(requirements, receipts, ContractKinds.Output,
            (r, sanitized) => r.Disposition == VerificationDisposition.Unknown && r.ContentHashes is { Count: > 0 } ? VerificationDisposition.Passed : sanitized);

        if (folded is null) return ArtifactDisposition.Unknown;

        return folded switch
        {
            VerificationDisposition.Failed => ArtifactDisposition.CaptureFailed,
            VerificationDisposition.Passed => ArtifactDisposition.Captured,
            VerificationDisposition.NotApplicable => ArtifactDisposition.NothingExpected,
            _ => ArtifactDisposition.Unknown,
        };
    }

    /// <summary>
    /// The delivery-kind fold. No delivery requirement → <see cref="DeliveryDisposition.NotRequired"/>. Receipts
    /// fold per requirement (<see cref="FoldKind"/>); the folded verdict maps, pinned: <c>Passed</c> → Delivered;
    /// <c>Waived</c> → WaivedByPolicy (never Delivered — a waiver is not a delivery); <c>Failed</c> →
    /// PolicyBlocked (the one definite-negative delivery state today IS the policy-parked path — a future
    /// non-policy failure mode gets its own encoding, never a reuse of this one); <c>NotApplicable</c> →
    /// NotRequired (an AUTHORIZED "no delivery owed after all"); everything else — including an unanswered
    /// REQUIRED delivery — → Unknown.
    /// </summary>
    private static DeliveryDisposition ClassifyDelivery(IReadOnlyList<RequirementEnvelope> requirements, IReadOnlyList<ReceiptEnvelope> receipts)
    {
        var folded = FoldKind(requirements, receipts, ContractKinds.Delivery);

        if (folded is null) return DeliveryDisposition.NotRequired;

        return folded switch
        {
            VerificationDisposition.Passed => DeliveryDisposition.Delivered,
            VerificationDisposition.Waived => DeliveryDisposition.WaivedByPolicy,
            VerificationDisposition.Failed => DeliveryDisposition.PolicyBlocked,
            VerificationDisposition.NotApplicable => DeliveryDisposition.NotRequired,
            _ => DeliveryDisposition.Unknown,
        };
    }
}
