using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

[Trait("Category", "Unit")]
public class LegacyProviderExceptionClassifierTests
{
    [Theory]
    [InlineData(ArtifactStorageErrorCode.Missing, false, (int)LegacyProviderExceptionDisposition.Missing)]
    [InlineData(ArtifactStorageErrorCode.IntegrityMismatch, false, (int)LegacyProviderExceptionDisposition.Corrupt)]
    [InlineData(ArtifactStorageErrorCode.Corrupt, false, (int)LegacyProviderExceptionDisposition.Corrupt)]
    [InlineData(ArtifactStorageErrorCode.Unauthorized, true, (int)LegacyProviderExceptionDisposition.Rejected)]
    [InlineData(ArtifactStorageErrorCode.Forbidden, true, (int)LegacyProviderExceptionDisposition.Rejected)]
    [InlineData(ArtifactStorageErrorCode.Unsupported, true, (int)LegacyProviderExceptionDisposition.Rejected)]
    [InlineData(ArtifactStorageErrorCode.InvalidRequest, true, (int)LegacyProviderExceptionDisposition.Rejected)]
    [InlineData(ArtifactStorageErrorCode.Unavailable, true, (int)LegacyProviderExceptionDisposition.Retryable)]
    public void Typed_result_errors_have_one_provider_neutral_disposition(ArtifactStorageErrorCode code, bool retryable,
        int expected)
    {
        ((int)LegacyProviderExceptionClassifier.Classify(new ArtifactStorageError(code, "redacted", retryable))).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ArtifactStorageErrorCode.Missing, false, (int)LegacyProviderExceptionDisposition.Missing)]
    [InlineData(ArtifactStorageErrorCode.Corrupt, false, (int)LegacyProviderExceptionDisposition.Corrupt)]
    [InlineData(ArtifactStorageErrorCode.Forbidden, true, (int)LegacyProviderExceptionDisposition.Rejected)]
    [InlineData(ArtifactStorageErrorCode.ProviderFailure, true, (int)LegacyProviderExceptionDisposition.Retryable)]
    public void Exceptional_provider_markers_use_the_same_table(ArtifactStorageErrorCode code, bool retryable,
        int expected)
    {
        ((int)LegacyProviderExceptionClassifier.Classify(new MarkedProviderException(code, retryable))).ShouldBe(expected);
    }

    [Fact]
    public void Unmarked_exception_is_a_programming_fault() =>
        LegacyProviderExceptionClassifier.Classify(new InvalidOperationException()).ShouldBe(LegacyProviderExceptionDisposition.ProgrammingFault);

    [Fact]
    public void Pre_budget_command_json_keeps_additive_defaults_and_wire_enums_keep_their_numbers()
    {
        var request = JsonSerializer.Deserialize<LegacyPlacementAdoptionRequest>("""
            {"TeamId":"00000000-0000-0000-0000-000000000001","ActorId":"00000000-0000-0000-0000-000000000002","ProfileId":"00000000-0000-0000-0000-000000000003","BatchSize":7,"Cursor":null}
            """).ShouldNotBeNull();

        request.ByteBudget.ShouldBe(LegacyPlacementAdoptionLimits.DefaultBytesPerPass);
        request.TimeBudget.ShouldBe(LegacyPlacementAdoptionLimits.DefaultTimePerPass);
        ((int)LegacyPlacementAdoptionYieldReasonValue.ProviderRetryable).ShouldBe(4);
        ((int)LegacyPlacementAdoptionPassOutcome.Interrupted).ShouldBe(3);
        ((int)LegacyPlacementAdoptionPassFailureCode.AdmissionEvidenceMissing).ShouldBe(6);
    }

    private sealed class MarkedProviderException(ArtifactStorageErrorCode code, bool retryable) : Exception, IArtifactStorageOperationalException
    {
        public ArtifactStorageErrorCode Code { get; } = code;
        public bool IsRetryable { get; } = retryable;
    }
}
