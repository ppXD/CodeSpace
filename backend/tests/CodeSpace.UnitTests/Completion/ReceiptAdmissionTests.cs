using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the ONE receipt-admission membrane (P1b-4 / Lock Clause 3) — identity, lineage and cardinality
/// integrity enforced before the pure fold ever sees a receipt. Pins: superseded-attempt exclusion, plan-version
/// and executable-set fencing, contract-hash binding, DISTINCT-target cardinality (duplicates can never fake
/// ExpectedCardinality), identity-less receipts flagged-not-dropped, orphan refs rejected, admitted receipts
/// byte-untouched.
/// </summary>
[Trait("Category", "Unit")]
public class ReceiptAdmissionTests
{
    private static readonly Guid PlanId = Guid.NewGuid();

    [Fact]
    public void A_superseded_attempts_receipt_never_reaches_a_fold()
    {
        var oldAttempt = Guid.NewGuid();
        var newAttempt = Guid.NewGuid();
        var active = Active("s1", newAttempt, ordinal: 2);

        var result = ReceiptAdmission.Admit(new[] { Receipt("acc:s1", oldAttempt, Unit("s1")) }, new[] { Requirement("acc:s1") }, Set(("s1", null)), active);

        result.Admitted.ShouldBeEmpty();
        result.Rejections.ShouldHaveSingleItem().Code.ShouldBe(ReceiptRejectionCodes.SupersededAttempt);
    }

    [Fact]
    public void A_receipt_from_a_superseded_plan_version_is_fenced()
    {
        var receipt = Receipt("acc:s1", Guid.NewGuid(), new WorkUnitRef { WorkPlanId = PlanId, PlanVersion = 1, UnitId = "s1" });
        var setAtV2 = ExecutableSet.Create(PlanId, 2, new[] { new ExecutableUnit { UnitId = "s1", ContractHash = null, Disposition = UnitDisposition.Carried } });

        ReceiptAdmission.Admit(new[] { receipt }, new[] { Requirement("acc:s1") }, setAtV2, null)
            .Rejections.ShouldHaveSingleItem().Code.ShouldBe(ReceiptRejectionCodes.PlanVersionMismatch);
    }

    [Fact]
    public void A_cancelled_units_receipt_is_not_executable()
    {
        ReceiptAdmission.Admit(new[] { Receipt("acc:sX", Guid.NewGuid(), Unit("sX")) }, new[] { Requirement("acc:sX") }, Set(("s1", null)), null)
            .Rejections.ShouldHaveSingleItem().Code.ShouldBe(ReceiptRejectionCodes.UnitNotExecutable);
    }

    [Fact]
    public void A_contract_hash_mismatch_is_an_amended_obligation()
    {
        var receipt = Receipt("acc:s1", Guid.NewGuid(), Unit("s1", contractHash: "sha256/canonical-json-v1:OLD"));

        ReceiptAdmission.Admit(new[] { receipt }, new[] { Requirement("acc:s1") }, Set(("s1", "sha256/canonical-json-v1:NEW")), null)
            .Rejections.ShouldHaveSingleItem().Code.ShouldBe(ReceiptRejectionCodes.ContractHashMismatch);
    }

    [Fact]
    public void Duplicate_targets_can_never_fake_cardinality()
    {
        var attempt = Guid.NewGuid();
        var receipts = new[]
        {
            Receipt("del:run", attempt, Unit("s1"), ContractKinds.Delivery) with { TargetRef = "repo-A", EvidenceRef = Guid.NewGuid() },
            Receipt("del:run", attempt, Unit("s1"), ContractKinds.Delivery) with { TargetRef = "repo-A", EvidenceRef = Guid.NewGuid() },   // duplicate
            Receipt("del:run", attempt, Unit("s1"), ContractKinds.Delivery) with { TargetRef = "repo-B", EvidenceRef = Guid.NewGuid() },
        };

        var result = ReceiptAdmission.Admit(receipts, new[] { Requirement("del:run", ContractKinds.Delivery) }, Set(("s1", null)), null);

        result.Admitted.Count.ShouldBe(2, "cardinality counts DISTINCT targets");
        result.Rejections.ShouldHaveSingleItem().Code.ShouldBe(ReceiptRejectionCodes.DuplicateTarget);
    }

    [Fact]
    public void Null_target_receipts_key_on_their_attempt()
    {
        var attempt = Guid.NewGuid();
        var receipts = new[] { Receipt("acc:s1", attempt, Unit("s1")), Receipt("acc:s1", attempt, Unit("s1")) };

        ReceiptAdmission.Admit(receipts, new[] { Requirement("acc:s1") }, Set(("s1", null)), null)
            .Admitted.Count.ShouldBe(1, "two null-target receipts from one attempt for one requirement are one attestation");
    }

    [Fact]
    public void An_identity_less_receipt_is_flagged_not_dropped()
    {
        var result = ReceiptAdmission.Admit(new[] { Receipt("acc:s1", Guid.NewGuid(), workUnit: null) with { EvidenceRef = Guid.NewGuid() } }, new[] { Requirement("acc:s1") }, Set(("s1", null)), null);

        result.Admitted.Count.ShouldBe(1, "Legacy/Shadow tolerate identity-less receipts — Enforced refuses downstream (Lock Clause 3)");
        var flag = result.Rejections.ShouldHaveSingleItem();
        flag.Code.ShouldBe(ReceiptRejectionCodes.MissingIdentity);
        flag.Warning.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void An_orphan_requirement_ref_is_rejected()
    {
        ReceiptAdmission.Admit(new[] { Receipt("ghost", Guid.NewGuid(), Unit("s1")) }, new[] { Requirement("acc:s1") }, Set(("s1", null)), null)
            .Rejections.ShouldHaveSingleItem().Code.ShouldBe(ReceiptRejectionCodes.OrphanRequirement);
    }

    [Fact]
    public void An_unevidenced_required_pass_is_capped_at_InfraUnknown()
    {
        // Admission batch 2: an unauditable pass can never mint Solved — the check may have run, its output
        // cannot be examined. Only the POSITIVE claim is capped.
        var requirements = new[] { Requirement("acc:s1") };
        var receipts = new[] { Receipt("acc:s1", Guid.NewGuid(), Unit("s1")) };   // Passed, no EvidenceRef

        var result = ReceiptAdmission.Admit(receipts, requirements, Set(("s1", null)), null);

        result.Admitted.ShouldHaveSingleItem().Disposition.ShouldBe(VerificationDisposition.InfraUnknown);
        var flag = result.Rejections.ShouldHaveSingleItem();
        flag.Code.ShouldBe(ReceiptRejectionCodes.MissingEvidence);
        flag.Warning.ShouldBeTrue();
    }

    [Fact]
    public void An_evidenced_pass_and_an_unevidenced_failure_are_untouched()
    {
        var requirements = new[] { Requirement("acc:s1"), Requirement("acc:s2") };
        var receipts = new[]
        {
            Receipt("acc:s1", Guid.NewGuid(), Unit("s1")) with { EvidenceRef = Guid.NewGuid() },
            Receipt("acc:s2", Guid.NewGuid(), Unit("s2")) with { Disposition = VerificationDisposition.Failed },
        };

        var result = ReceiptAdmission.Admit(receipts, requirements, Set(("s1", null), ("s2", null)), null);

        result.Admitted.Count.ShouldBe(2);
        result.Admitted[0].Disposition.ShouldBe(VerificationDisposition.Passed, "evidence present — the pass stands");
        result.Admitted[1].Disposition.ShouldBe(VerificationDisposition.Failed, "an unevidenced FAILURE is the safe direction — it can never inflate");
        result.Rejections.ShouldBeEmpty();
    }

    [Fact]
    public void An_optional_requirements_unevidenced_pass_is_not_capped()
    {
        var requirements = new[] { Requirement("acc:opt") with { Requiredness = Requiredness.Optional } };
        var receipts = new[] { Receipt("acc:opt", Guid.NewGuid(), Unit("s1")) };

        ReceiptAdmission.Admit(receipts, requirements, null, null)
            .Admitted.ShouldHaveSingleItem().Disposition.ShouldBe(VerificationDisposition.Passed, "the evidence law binds REQUIRED contracts");
    }

    [Fact]
    public void Admitted_receipts_pass_through_byte_untouched()
    {
        var receipt = Receipt("acc:s1", Guid.NewGuid(), Unit("s1")) with { TargetRef = "repo-A", ContentHashes = new[] { "abc" }, EvidenceRef = Guid.NewGuid() };

        var result = ReceiptAdmission.Admit(new[] { receipt }, new[] { Requirement("acc:s1") }, Set(("s1", null)), null);

        result.Admitted.ShouldHaveSingleItem().ShouldBeSameAs(receipt);
    }

    // ── Builders ──

    private static WorkUnitRef Unit(string unitId, string? contractHash = null) => new() { WorkPlanId = PlanId, PlanVersion = 1, UnitId = unitId, ContractHash = contractHash };

    private static ExecutableSet Set(params (string UnitId, string? Hash)[] units) =>
        ExecutableSet.Create(PlanId, 1, units.Select(u => new ExecutableUnit { UnitId = u.UnitId, ContractHash = u.Hash, Disposition = UnitDisposition.New }).ToList());

    private static IReadOnlyDictionary<UnitKey, AttemptProjection> Active(string unitId, Guid attemptId, int ordinal) =>
        new Dictionary<UnitKey, AttemptProjection>
        {
            [new UnitKey(PlanId, 1, unitId)] = new AttemptProjection { AttemptId = attemptId, UnitId = unitId, WorkUnit = new WorkUnitRef { WorkPlanId = PlanId, PlanVersion = 1, UnitId = unitId }, AttemptOrdinal = ordinal, State = AttemptState.Authorized },
        };

    private static RequirementEnvelope Requirement(string requirementRef, string kind = ContractKinds.Acceptance) => new()
    {
        RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.Required, Authority = ContractAuthority.Operator, ContractSchemaVersion = "1",
    };

    private static ReceiptEnvelope Receipt(string requirementRef, Guid attemptId, WorkUnitRef? workUnit, string kind = ContractKinds.Acceptance) => new()
    {
        RequirementRef = requirementRef, AttemptId = attemptId, WorkUnit = workUnit, Kind = kind,
        Disposition = VerificationDisposition.Passed, Authority = ContractAuthority.ServerPolicy, ObservedAt = DateTimeOffset.UnixEpoch,
    };
}
