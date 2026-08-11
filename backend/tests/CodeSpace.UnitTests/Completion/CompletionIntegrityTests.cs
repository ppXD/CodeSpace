using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: <see cref="CompletionIntegrity"/> — the ONE predicate the terminal authority and the shadow's
/// would-be decision share to refuse a CleanSuccess built over unverifiable evidence. Pins what IS a violation
/// (an identity-less receipt folded under Shadow tolerance, an adapter contract error, an unsupported requirement
/// schema version) and — just as load-bearing — what is NOT: an unevidenced-pass warning (admission already caps
/// it at InfraUnknown, its honest degradation reaches the decision on its own) and hard rejections (the dropped
/// receipt's obligation stays owed, so the decision already reflects it).
/// </summary>
[Trait("Category", "Unit")]
public class CompletionIntegrityTests
{
    [Fact]
    public void An_identity_less_receipt_folded_under_tolerance_is_a_violation()
    {
        var rejection = new ReceiptRejection(Receipt("acc:s1"), ReceiptRejectionCodes.MissingIdentity, "no WorkUnitRef", Warning: true);

        var violations = CompletionIntegrity.Violations(new[] { rejection }, Array.Empty<string>(), new[] { Requirement("acc:s1") });

        violations.ShouldHaveSingleItem().ShouldContain("acc:s1");
    }

    [Fact]
    public void An_unevidenced_pass_warning_is_NOT_a_violation()
    {
        // Admission already capped the receipt at InfraUnknown — the decision sees the honest degradation. Parking
        // on top of it would punish a run whose OTHER, evidenced receipt legitimately satisfied the requirement.
        var rejection = new ReceiptRejection(Receipt("acc:s1"), ReceiptRejectionCodes.MissingEvidence, "capped at InfraUnknown", Warning: true);

        CompletionIntegrity.Violations(new[] { rejection }, Array.Empty<string>(), new[] { Requirement("acc:s1") }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ReceiptRejectionCodes.SupersededAttempt)]
    [InlineData(ReceiptRejectionCodes.OrphanRequirement)]
    [InlineData(ReceiptRejectionCodes.PlanVersionMismatch)]
    [InlineData(ReceiptRejectionCodes.ContractHashMismatch)]
    [InlineData(ReceiptRejectionCodes.UnitNotExecutable)]
    [InlineData(ReceiptRejectionCodes.DuplicateTarget)]
    public void A_hard_rejection_is_NOT_a_violation(string code)
    {
        // The receipt was DROPPED from the fold — its obligation stays owed and the decision already reflects it.
        // Lineage hygiene (a superseded attempt's stale receipt) is a NORMAL part of a healthy run, not taint.
        var rejection = new ReceiptRejection(Receipt("acc:s1"), code, "dropped");

        CompletionIntegrity.Violations(new[] { rejection }, Array.Empty<string>(), new[] { Requirement("acc:s1") }).ShouldBeEmpty();
    }

    [Fact]
    public void An_adapter_contract_error_is_a_violation()
    {
        var violations = CompletionIntegrity.Violations(Array.Empty<ReceiptRejection>(), new[] { "decision 7: 2 unit(s) but 3 attempt id(s) — positional contract broken" }, new[] { Requirement("acc:s1") });

        violations.ShouldHaveSingleItem().ShouldContain("positional contract broken");
    }

    [Fact]
    public void An_unsupported_requirement_schema_version_is_a_violation()
    {
        var violations = CompletionIntegrity.Violations(Array.Empty<ReceiptRejection>(), Array.Empty<string>(), new[] { Requirement("acc:s1"), Requirement("del:s1") with { ContractSchemaVersion = "2038-draft" } });

        violations.ShouldHaveSingleItem().ShouldContain("2038-draft");
    }

    [Fact]
    public void The_supported_schema_version_is_pinned_to_what_staking_writes()
    {
        // SupervisorUnitContract.Stake writes the literal "1" — the reader and the writer must name the SAME
        // version, or every staked requirement becomes a violation the moment one side drifts.
        CompletionIntegrity.SupportedContractSchemaVersion.ShouldBe("1");
        SupervisorRequirementSchemaVersion().ShouldBe(CompletionIntegrity.SupportedContractSchemaVersion);
    }

    [Fact]
    public void A_clean_compose_has_no_violations()
    {
        CompletionIntegrity.Violations(Array.Empty<ReceiptRejection>(), Array.Empty<string>(), new[] { Requirement("acc:s1") }).ShouldBeEmpty();
    }

    private static string SupervisorRequirementSchemaVersion() =>
        Core.Services.Supervisor.SupervisorUnitContract.BuildStakedRequirements(new[] { ("s1", "h", true) }, ContractAuthority.ModelProposal)[0].ContractSchemaVersion;

    private static RequirementEnvelope Requirement(string requirementRef) => new()
    {
        RequirementRef = requirementRef, Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1",
    };

    private static ReceiptEnvelope Receipt(string requirementRef) => new()
    {
        RequirementRef = requirementRef, Kind = ContractKinds.Acceptance, AttemptId = Guid.NewGuid(), WorkUnit = null,
        Disposition = VerificationDisposition.Passed, Authority = ContractAuthority.ServerPolicy, ObservedAt = DateTimeOffset.UnixEpoch,
    };
}
