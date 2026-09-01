using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

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

    private sealed class MarkedProviderException(ArtifactStorageErrorCode code, bool retryable) : Exception, IArtifactStorageOperationalException
    {
        public ArtifactStorageErrorCode Code { get; } = code;
        public bool IsRetryable { get; } = retryable;
    }
}
