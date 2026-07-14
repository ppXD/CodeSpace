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
    public const string DuplicateTarget = "duplicate-target";
    public const string MissingIdentity = "missing-identity";
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
/// set at the CURRENT plan version, carry the unit's contract hash when both sides have one, come from the
/// OPERATIONAL ACTIVE attempt (a superseded attempt's receipt never reaches a fold — Lock Clause 3), and attest a
/// DISTINCT target (duplicate receipts for one target collapse to the first, so ExpectedCardinality can never be
/// faked by repetition). An identity-less receipt (no <see cref="ReceiptEnvelope.WorkUnit"/>) is admitted with a
/// WARNING — tolerable under Legacy/Shadow, fatal under Enforced, decided by the composer, never here. Batch 2
/// (EvidenceRef readback, EvaluatorVersion allowlist, generation/lease currency) lands with P3a's substrate; the
/// codes are reserved now so admission only ever TIGHTENS.
/// </summary>
public static class ReceiptAdmission
{
    public static ReceiptAdmissionResult Admit(IReadOnlyList<ReceiptEnvelope> receipts, IReadOnlyList<RequirementEnvelope> requirements, ExecutableSet? executableSet, IReadOnlyDictionary<UnitKey, AttemptProjection>? operationalActive)
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

                if (receipt.WorkUnit.ContractHash is not null && unit.ContractHash is not null && receipt.WorkUnit.ContractHash != unit.ContractHash)
                {
                    rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.ContractHashMismatch, $"unit '{unit.UnitId}': receipt attests contract {receipt.WorkUnit.ContractHash} but the executable contract is {unit.ContractHash}"));
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

            var targetKey = receipt.TargetRef ?? $"attempt:{receipt.AttemptId}";

            if (!seenTargets.Add((receipt.RequirementRef, targetKey)))
            {
                rejections.Add(new ReceiptRejection(receipt, ReceiptRejectionCodes.DuplicateTarget, $"requirement '{receipt.RequirementRef}': a receipt for target '{targetKey}' was already admitted — cardinality counts DISTINCT targets"));
                continue;
            }

            admitted.Add(receipt);
        }

        return new ReceiptAdmissionResult(admitted, rejections);
    }
}
