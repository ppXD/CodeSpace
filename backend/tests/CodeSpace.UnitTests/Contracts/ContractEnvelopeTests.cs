using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Contracts;

/// <summary>
/// F0b PR-1 pins: the contract envelopes' WIRE vocabulary (enum member names + registry keys are tape/CAS-bound —
/// a rename orphans every persisted envelope), the null-omitted serialization contract, and the ONE typed
/// classification every consumer must share.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ContractEnvelopeTests
{
    [Fact]
    public void The_kind_registry_keys_are_pinned()
    {
        // Persisted envelopes reference these strings forever — a rename is a data migration, never a refactor.
        ContractKinds.Acceptance.ShouldBe("acceptance");
        ContractKinds.Delivery.ShouldBe("delivery");
        ContractKinds.Output.ShouldBe("output");
    }

    [Fact]
    public void The_enum_wire_names_are_pinned()
    {
        // String-enum converters put the NAMES on the wire; renames silently orphan persisted values.
        Enum.GetNames<VerificationDisposition>().ShouldBe(new[] { "Passed", "Failed", "NotApplicable", "InfraUnknown", "HumanReviewRequired", "Waived", "Unknown" });
        Enum.GetNames<ContractAuthority>().ShouldBe(new[] { "Operator", "ServerPolicy", "ModelProposal" });
        Enum.GetNames<OutputExpectation>().ShouldBe(new[] { "GitChange", "TypedArtifact", "NoOutputExpected", "HumanReviewRequired" });
        Enum.GetNames<Requiredness>().ShouldBe(new[] { "Required", "Optional", "OperatorAuthorizedNotApplicable", "ServerPolicyAuthorizedNotApplicable" });   // P2b-2 (Lock Clause 4): authorized-NA staking vocabulary
        Enum.GetNames<ExecutionDisposition>().ShouldBe(new[] { "Completed", "ForcedStop", "Crashed", "Cancelled" });
        Enum.GetNames<OutcomeDisposition>().ShouldBe(new[] { "Solved", "Unsolved", "Abstained", "Unknown" });
        Enum.GetNames<ArtifactDisposition>().ShouldBe(new[] { "Captured", "CaptureFailed", "NothingExpected", "Unknown" });
        Enum.GetNames<DeliveryDisposition>().ShouldBe(new[] { "Delivered", "PolicyBlocked", "WaivedByPolicy", "NotRequired", "Unknown" });
        Enum.GetNames<CompletionBasis>().ShouldBe(new[] { "ContractDerived", "LegacyUnknown" });
    }

    [Fact]
    public void A_minimal_envelope_serializes_with_no_null_keys()
    {
        var requirement = new RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.Operator, ContractSchemaVersion = "contract-v1" };
        var requirementJson = JsonSerializer.Serialize(requirement, AgentJson.Options);

        requirementJson.ShouldNotContain("null", customMessage: "null-omitted means OMITTED — spurious null keys would break byte-stable persisted shapes as optional fields arrive");
        requirementJson.ShouldNotContain("specRef");
        requirementJson.ShouldNotContain("expectedCardinality");

        var receipt = new ReceiptEnvelope { RequirementRef = "acceptance:s1", AttemptId = Guid.NewGuid(), Kind = ContractKinds.Acceptance, Disposition = VerificationDisposition.Passed, Authority = ContractAuthority.ServerPolicy, ObservedAt = DateTimeOffset.UnixEpoch };
        var receiptJson = JsonSerializer.Serialize(receipt, AgentJson.Options);

        receiptJson.ShouldNotContain("workUnit");
        receiptJson.ShouldNotContain("evidenceRef");
        receiptJson.ShouldContain("\"disposition\":\"Passed\"", customMessage: "the enum NAME is the wire value");

        var assessment = new CompletionAssessment { Basis = CompletionBasis.ContractDerived, Execution = ExecutionDisposition.Completed, Outcome = OutcomeDisposition.Solved, Verification = VerificationDisposition.Passed, Artifact = ArtifactDisposition.Captured, Delivery = DeliveryDisposition.Delivered };
        JsonSerializer.Serialize(assessment, AgentJson.Options).ShouldNotContain("forcedStopReason");
    }

    [Fact]
    public void Envelopes_round_trip_through_the_shared_agent_json_options()
    {
        var receipt = new ReceiptEnvelope
        {
            RequirementRef = "delivery:run", Kind = ContractKinds.Delivery,
            WorkUnit = new WorkUnitRef { WorkPlanId = Guid.NewGuid(), PlanVersion = 2, UnitId = "s1", ContractHash = "abc" },
            AttemptId = Guid.NewGuid(), Generation = 3, ContentHashes = new[] { "sha1" },
            Disposition = VerificationDisposition.Waived, Authority = ContractAuthority.Operator,
            EvidenceRef = Guid.NewGuid(), EvaluatorVersion = "grader-v1", ObservedAt = DateTimeOffset.UnixEpoch,
        };

        var json = JsonSerializer.Serialize(receipt, AgentJson.Options);
        var parsed = JsonSerializer.Deserialize<ReceiptEnvelope>(json, AgentJson.Options);

        JsonSerializer.Serialize(parsed, AgentJson.Options).ShouldBe(json, "byte-identical over the full round-trip — the tape is the source of truth (record equality can't cover the collection member)");
        parsed!.Disposition.ShouldBe(VerificationDisposition.Waived);
        parsed.WorkUnit!.ContractHash.ShouldBe("abc");
    }

    // ── The ONE typed classification (VerificationDispositions) ────────────────────

    [Theory]
    [InlineData(true, "tests-passed", true, VerificationDisposition.Passed)]
    [InlineData(false, "tests-failed-exit-1", true, VerificationDisposition.Failed)]
    [InlineData(false, "clone-failed: fatal", true, VerificationDisposition.InfraUnknown)]
    [InlineData(false, "grade-error: no binary", true, VerificationDisposition.InfraUnknown)]
    [InlineData(null, null, true, VerificationDisposition.Unknown)]
    public void Classify_maps_todays_signals_onto_the_typed_disposition(bool? passed, string? detail, bool workPresent, VerificationDisposition expected)
    {
        VerificationDispositions.Classify(passed, detail, workPresent).ShouldBe(expected);
    }

    [Theory]
    [InlineData(VerificationDisposition.Passed, PublishAcceptanceState.Passed)]
    [InlineData(VerificationDisposition.Failed, PublishAcceptanceState.Failed)]
    [InlineData(VerificationDisposition.InfraUnknown, PublishAcceptanceState.Failed)]   // byte-compat with the executor's historical bool switch — reclassifying shifts the delivery scorecard
    [InlineData(VerificationDisposition.Waived, PublishAcceptanceState.NotApplicable)]  // WAIVED ≠ PASSED anywhere a verdict reads as objective truth
    [InlineData(VerificationDisposition.Unknown, PublishAcceptanceState.NotApplicable)]
    [InlineData(VerificationDisposition.HumanReviewRequired, PublishAcceptanceState.NotApplicable)]
    public void The_legacy_projection_is_byte_compatible_and_never_launders_a_waive(VerificationDisposition disposition, PublishAcceptanceState expected)
    {
        VerificationDispositions.ToLegacyAcceptanceState(disposition).ShouldBe(expected);
    }
}
