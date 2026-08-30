using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Contracts;

/// <summary>
/// Workflow Run Data v1 pins: run-owned names are unmistakable in SQL and every content reference carries enough
/// identity to reject partial, corrupt, cross-attempt, or unsupported data instead of silently treating it as empty.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRunDataContractTests
{
    [Fact]
    public void Every_run_owned_table_name_is_prefixed_and_unique()
    {
        WorkflowRunDataNames.All.ShouldNotBeEmpty();
        WorkflowRunDataNames.All.ShouldAllBe(name => name.StartsWith(WorkflowRunDataNames.Prefix, StringComparison.Ordinal));
        WorkflowRunDataNames.All.Distinct(StringComparer.Ordinal).Count().ShouldBe(WorkflowRunDataNames.All.Count);

        WorkflowRunDataNames.ModelCall.ShouldBe("workflow_run_model_call");
        WorkflowRunDataNames.ModelCallAttempt.ShouldBe("workflow_run_model_call_attempt");
        WorkflowRunDataNames.HarnessExecution.ShouldBe("workflow_run_harness_execution");
        WorkflowRunDataNames.NativeRecord.ShouldBe("workflow_run_native_record");
        WorkflowRunDataNames.LogSegment.ShouldBe("workflow_run_log_segment");
        WorkflowRunDataNames.DataManifest.ShouldBe("workflow_run_data_manifest");
    }

    [Theory]
    [InlineData("storage_profile")]
    [InlineData("storage_route")]
    [InlineData("artifact_object")]
    [InlineData("artifact_location")]
    public void Global_storage_aggregates_are_not_run_owned_names(string tableName)
    {
        WorkflowRunDataNames.IsRunOwned(tableName).ShouldBeFalse("a WorkflowRunId foreign key does not transfer aggregate ownership to the run plane");
    }

    [Fact]
    public void Capture_completeness_wire_names_and_strict_read_boundary_are_closed()
    {
        Enum.GetNames<WorkflowRunCaptureCompleteness>().ShouldBe(new[] { "Exact", "RedactedExact", "Partial", "Unavailable", "Corrupt", "LegacyUnknown" });
        WorkflowRunCaptureCompleteness.Exact.IsStrictlyReadable().ShouldBeTrue();
        WorkflowRunCaptureCompleteness.RedactedExact.IsStrictlyReadable().ShouldBeTrue();
        WorkflowRunCaptureCompleteness.Partial.IsStrictlyReadable().ShouldBeFalse();
        WorkflowRunCaptureCompleteness.Unavailable.IsStrictlyReadable().ShouldBeFalse();
        WorkflowRunCaptureCompleteness.Corrupt.IsStrictlyReadable().ShouldBeFalse();
        WorkflowRunCaptureCompleteness.LegacyUnknown.IsStrictlyReadable().ShouldBeFalse();

        JsonSerializer.Serialize(WorkflowRunCaptureCompleteness.RedactedExact, AgentJson.Options).ShouldBe("\"RedactedExact\"");
    }

    /// <summary>
    /// The two nouns for a file a RUN produced. Both were held out of the registry on the reasoning migration 0175
    /// wrote down — a facet nothing advances "would sit at expected=0 forever and read as complete" — and 0172, which
    /// shipped BEFORE it, had already made that impossible: an unadvanced facet is minted with a NULL expectation under
    /// <see cref="WorkflowRunCaptureCompleteness.LegacyUnknown"/>, which the database refuses every complete verdict
    /// over. Registration reserves the noun; it mints no row and states nothing.
    /// </summary>
    [Theory]
    [InlineData(WorkflowRunDataOwnerKinds.NodeOutput)]
    [InlineData(WorkflowRunDataOwnerKinds.Deliverable)]
    public void A_file_a_run_produced_is_a_registered_owner_noun(string ownerKind)
    {
        WorkflowRunDataOwnerKinds.All.ShouldContain(ownerKind);
        WorkflowRunDataOwnerKinds.IsSupported(ownerKind).ShouldBeTrue("a reference to a run-produced file must validate, or the only owner it can name is a plane that did not produce it");
    }

    [Fact]
    public void A_valid_artifact_reference_round_trips_with_run_and_attempt_identity()
    {
        var reference = ValidReference() with
        {
            WorkUnit = new WorkUnitRef { WorkPlanId = Guid.NewGuid(), PlanVersion = 4, UnitId = "unit-2", ContractHash = "contract-hash" },
            AttemptId = Guid.NewGuid(), AttemptOrdinal = 2, ExecutionGeneration = 3,
        };

        reference.Validate().ShouldBeEmpty();

        var json = JsonSerializer.Serialize(reference, AgentJson.Options);
        var roundTrip = JsonSerializer.Deserialize<WorkflowRunArtifactRefV1>(json, AgentJson.Options);

        roundTrip.ShouldNotBeNull();
        roundTrip!.WorkflowRunId.ShouldBe(reference.WorkflowRunId);
        roundTrip.AttemptId.ShouldBe(reference.AttemptId);
        roundTrip.ExecutionGeneration.ShouldBe(3);
        roundTrip.Completeness.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
        JsonSerializer.Serialize(roundTrip, AgentJson.Options).ShouldBe(json);
    }

    [Fact]
    public void Invalid_or_unsupported_identity_fails_closed()
    {
        var reference = ValidReference() with
        {
            ContractVersion = 99,
            WorkflowRunId = Guid.Empty,
            OwnerKind = "future-owner-without-a-version",
            OwnerId = " ",
            ArtifactId = Guid.Empty,
            Digest = new string('A', 64),
            SizeBytes = -1,
            AttemptOrdinal = 0,
            ExecutionGeneration = 1,
        };

        var errors = reference.Validate();

        errors.Count.ShouldBe(9);
        errors.ShouldContain(error => error.Contains("contractVersion", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("workflowRunId", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("ownerKind", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("digest", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("attemptId", StringComparison.Ordinal));
    }

    private static WorkflowRunArtifactRefV1 ValidReference() => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        WorkflowRunId = Guid.NewGuid(),
        OwnerKind = WorkflowRunDataOwnerKinds.ModelCall,
        OwnerId = "call-01",
        Role = "response.canonical",
        ArtifactId = Guid.NewGuid(),
        DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
        Digest = new string('a', 64),
        SizeBytes = 42,
        Completeness = WorkflowRunCaptureCompleteness.Exact,
    };
}
