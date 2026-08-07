using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>Why a receipt was rejected or flagged by admission. Codes are wire-stable (they land on assessments and journals) — renaming one is a data migration.</summary>
public static class ReceiptRejectionCodes
{
    public const string OrphanRequirement = "orphan-requirement";
    public const string UnitNotExecutable = "unit-not-executable";
    public const string PlanVersionMismatch = "plan-version-mismatch";
    public const string ContractHashMismatch = "contract-hash-mismatch";
    public const string SupersededAttempt = "superseded-attempt";
    public const string SupersededContract = "superseded-contract";
    public const string DuplicateTarget = "duplicate-target";
    public const string MissingIdentity = "missing-identity";
    public const string MissingEvidence = "missing-evidence";
}

public sealed record ReceiptRejection(ReceiptEnvelope Receipt, string Code, string Reason, bool Warning = false);

public sealed record ReceiptAdmissionResult(IReadOnlyList<ReceiptEnvelope> Admitted, IReadOnlyList<ReceiptRejection> Rejections)
{
    /// <summary>Hard rejections only — warnings (Shadow-tolerable, Enforced-fatal per Lock Clause 3) are the composer's policy call.</summary>
    public IEnumerable<ReceiptRejection> Errors => Rejections.Where(r => !r.Warning);
}

/// <summary>
/// THE one admission membrane between collected receipts and the reducer (P1b-4 / v4.2 §四). The reducer is
/// deliberately a pure fold — identity, lineage and cardinality integrity are enforced HERE, once, for every
/// consumer: a receipt must answer a KNOWN requirement (ref + kind), belong to a unit of the CURRENT executable
/// set at the CURRENT plan version, attest the SAME contract its requirement STAKED when both sides carry a hash
/// (same-domain: the requirement's SpecHash and the receipt's dispatch stamp are both attempt-grain — never the
/// executable unit's plan-grain hash, see the P1 note inside), come from the OPERATIONAL ACTIVE attempt (a
/// superseded attempt's receipt never reaches a fold — Lock Clause 3), attest a DISTINCT target (duplicate
/// receipts for one target collapse to the first, so ExpectedCardinality can never be faked by repetition). An
/// identity-less receipt (no <see cref="ReceiptEnvelope.WorkUnit"/>) is admitted with a WARNING — tolerable under
/// Legacy/Shadow, fatal under Enforced, decided by the composer, never here. Batch 2 (EvidenceRef readback,
/// EvaluatorVersion allowlist, generation/lease currency) lands with P3a's substrate; the codes are reserved now
/// so admission only ever TIGHTENS.
/// </summary>
public static class ReceiptAdmission
{
    public static ReceiptAdmissionResult Admit(IReadOnlyList<ReceiptEnvelope> receipts, IReadOnlyList<RequirementEnvelope> requirements, ExecutableSet? executableSet, IReadOnlyDictionary<UnitKey, AttemptProjection>? operationalActive, IReadOnlyDictionary<(string RequirementRef, string Kind), long>? currentRevisions = null)
    {
        var admitted = new List<ReceiptEnvelope>();
        var rejections = new List<ReceiptRejection>();
        var seenTargets = new HashSet<(string RequirementRef, string TargetKey)>();

        foreach (var receipt in receipts)
        {
            if (!requirements.Any(r => r.RequirementRef == receipt.RequirementRef && r.Kind == receipt.Kind))
            {
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.OrphanRequirement, $"no requirement matches ref '{receipt.RequirementRef}' kind '{receipt.Kind}'"));
                continue;
            }

            if (receipt.WorkUnit is null)
            {
                // Identity-less: Legacy/Shadow territory (Lock Clause 3) — flagged, not dropped; Enforced refuses downstream.
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.MissingIdentity, "receipt carries no WorkUnitRef — admissible under Legacy/Shadow only", Warning: true));
            }
            else if (executableSet is not null)
            {
                if (receipt.WorkUnit.WorkPlanId != executableSet.WorkPlanId || receipt.WorkUnit.PlanVersion != executableSet.PlanVersion)
                {
                    rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.PlanVersionMismatch, $"receipt is bound to plan {receipt.WorkUnit.WorkPlanId}v{receipt.WorkUnit.PlanVersion}; the executable set is {executableSet.WorkPlanId}v{executableSet.PlanVersion}"));
                    continue;
                }

                var unit = executableSet.Units.FirstOrDefault(u => u.UnitId == receipt.WorkUnit.UnitId);

                if (unit is null)
                {
                    rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.UnitNotExecutable, $"unit '{receipt.WorkUnit.UnitId}' is not in the current executable set (cancelled or never planned)"));
                    continue;
                }

            }

            if (receipt.WorkUnit is { } wu && operationalActive is not null
                && operationalActive.TryGetValue(new UnitKey(wu.WorkPlanId, wu.PlanVersion, wu.UnitId), out var active)
                && active.AttemptId != receipt.AttemptId)
            {
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.SupersededAttempt, $"unit '{wu.UnitId}': receipt is from attempt {receipt.AttemptId} but the operational active attempt is {active.AttemptId} (ordinal {active.AttemptOrdinal})"));
                continue;
            }

            // P1 (revision binding — identity where the hash check below is value): an acceptance receipt bound
            // to a revision the ledger has since re-staked answers a SUPERSEDED contract, even when a
            // revert-shaped amendment (A→B→A) makes the hashes collide. Only the ACCEPTANCE kind binds — the
            // delivery/output rows of one stake wave share its staleness (WorkUnitRef.RequirementRevision names
            // the wave by its acceptance row). Tolerant on every absent side: an unstamped receipt (legacy tape,
            // plan-less dispatch), a caller with no revision view (the tape mirror), and a key the ledger doesn't
            // know all pass through to the checks below.
            if (receipt.Kind == ContractKinds.Acceptance
                && receipt.WorkUnit?.RequirementRevision is { } bound
                && currentRevisions is not null && currentRevisions.TryGetValue((receipt.RequirementRef, receipt.Kind), out var currentRevision)
                && bound < currentRevision)
            {
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.SupersededContract, $"requirement '{receipt.RequirementRef}': receipt is bound to revision {bound} but the requirement has been re-staked at revision {currentRevision} — a receipt answering a superseded contract never reaches a fold"));
                continue;
            }

            // P1 (hash-domain separation): the contract binding is checked SAME-DOMAIN — the receipt's dispatch
            // stamp against the SpecHash its requirement STAKED. Both are attempt-grain: staging stakes the
            // EFFECTIVE contract (dispatch overrides included) and a retry's re-stake upserts the revised hash,
            // so for the operational-active attempt the two attest the same authorship. The retired comparison
            // read the executable unit's PLAN-grain hash (computed without overrides) instead, which made every
            // goal-override dispatch and every revised-instruction retry a GUARANTEED false park — while a true
            // contract amendment is caught earlier and honestly (a replan is a new PlanVersion → PlanVersionMismatch;
            // a stale attempt's receipt dies to SupersededAttempt above). Runs after the superseded filter so a
            // stale receipt keeps its honest code; compares only when both sides carry a hash, the same tolerance
            // the retired check had (tape-reconstructed receipts and legacy requirements carry none).
            if (receipt.WorkUnit?.ContractHash is { } attested
                && requirements.FirstOrDefault(r => r.RequirementRef == receipt.RequirementRef && r.Kind == receipt.Kind)?.SpecHash is { } staked
                && attested != staked)
            {
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.ContractHashMismatch, $"requirement '{receipt.RequirementRef}': receipt attests contract {attested} but the staked requirement's contract is {staked}"));
                continue;
            }

            var targetKey = receipt.TargetRef ?? $"attempt:{receipt.AttemptId}";

            if (!seenTargets.Add((receipt.RequirementRef, targetKey)))
            {
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.DuplicateTarget, $"requirement '{receipt.RequirementRef}': a receipt for target '{targetKey}' was already admitted — cardinality counts DISTINCT targets"));
                continue;
            }

            // P3a-2 (admission batch 2 — admission only ever TIGHTENS): a REQUIRED contract's PASS without
            // evidence is unauditable, so its disposition is CAPPED at InfraUnknown ("the check may have run,
            // its output cannot be examined"). Only the positive claim is capped: an unevidenced FAILURE is the
            // safe direction (it can never inflate), a Waiver's evidence is its human co-sign, and an authorized
            // exemption's is its authority — neither is oracle output. Pre-evidence tapes cap honestly too: an
            // unauditable pass is unauditable regardless of when it was recorded.
            if (receipt.EvidenceRef is null && receipt.Disposition == VerificationDisposition.Passed
                && requirements.Any(r => r.RequirementRef == receipt.RequirementRef && r.Kind == receipt.Kind && r.Requiredness == Requiredness.Required))
            {
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.MissingEvidence, $"requirement '{receipt.RequirementRef}': a REQUIRED contract's pass carries no evidence — disposition capped at InfraUnknown", Warning: true));
                admitted.Add(receipt with { Disposition = VerificationDisposition.InfraUnknown });
                continue;
            }

            admitted.Add(receipt);
        }

        return new ReceiptAdmissionResult(admitted, rejections);
    }
}
