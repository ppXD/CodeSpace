using AlibabaCloud.OSS.V2;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>Pins the safe, provider-neutral projection of official-SDK failures.</summary>
public sealed class AliyunOssErrorClassificationTests
{
    [Fact]
    public void A_deleted_bucket_is_a_durable_answer()
    {
        var error = AliyunOssErrors.FromException(Service(404, "NoSuchBucket"), "objects/x");

        error.Code.ShouldBe(ArtifactStorageErrorCode.Unavailable);
        error.Reason.ShouldBe(ArtifactStorageFailureReason.DestinationMissing);
        error.IsRetryable.ShouldBeFalse("retrying does not bring a deleted bucket back, and this bit is what lets abandonment close its records");
    }

    [Fact]
    public void A_server_fault_is_an_answer_about_the_moment()
    {
        var error = AliyunOssErrors.FromException(Service(500, "InternalError"), "objects/x");

        error.Code.ShouldBe(ArtifactStorageErrorCode.Unavailable);
        error.IsRetryable.ShouldBeTrue("a 5xx wears the same code as a deleted bucket, and retryability is the only thing telling them apart");
    }

    [Fact]
    public void A_network_fault_is_an_answer_about_the_moment()
    {
        var error = AliyunOssErrors.FromException(new OperationException("HeadObject", new RequestFailedException("secret network detail")), "objects/x");

        error.IsRetryable.ShouldBeTrue("an unreachable host says nothing about whether the namespace still exists");
        error.Reason.ShouldBe(ArtifactStorageFailureReason.NetworkUnavailable);
        error.Message.ShouldNotContain("secret network detail");
    }

    [Fact]
    public void An_unknown_sdk_wrapped_programming_fault_is_not_downgraded_to_a_provider_outage()
    {
        var exception = new OperationException("HeadObject", new InvalidOperationException("programming fault"));

        AliyunOssErrors.IsOperational(exception).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sdk_crc_mismatches_are_integrity_failures_not_network_failures(bool retryable)
    {
        Exception mismatch = retryable
            ? new InconsistentException("client", "server", "request")
            : new NoRetryableInconsistentException("client", "server", "request");

        var error = AliyunOssErrors.FromException(new OperationException("PutObject", mismatch), "objects/x");

        error.Code.ShouldBe(ArtifactStorageErrorCode.IntegrityMismatch);
        error.IsRetryable.ShouldBe(retryable);
    }

    [Theory]
    [InlineData("InvalidAccessKeyId", ArtifactStorageFailureReason.CredentialInvalid)]
    [InlineData("SignatureDoesNotMatch", ArtifactStorageFailureReason.SignatureMismatch)]
    [InlineData("InvalidSecurityToken", ArtifactStorageFailureReason.SecurityTokenInvalid)]
    [InlineData("SecurityTokenExpired", ArtifactStorageFailureReason.SecurityTokenExpired)]
    [InlineData("MissingSecurityToken", ArtifactStorageFailureReason.SecurityTokenMissing)]
    [InlineData("RequestTimeTooSkewed", ArtifactStorageFailureReason.ClockSkew)]
    [InlineData("AccessDenied", ArtifactStorageFailureReason.PermissionDenied)]
    public void Authentication_and_policy_failures_keep_a_safe_actionable_reason(string providerCode, ArtifactStorageFailureReason expected)
    {
        var error = AliyunOssErrors.FromException(Service(403, providerCode), "objects/x");

        error.Reason.ShouldBe(expected);
        error.ProviderCode.ShouldBe(providerCode);
        error.Message.ShouldNotContain("provider-secret");
    }

    private static OperationException Service(int status, string code) => new("HeadObject", new ServiceException(status, new Dictionary<string, string>
    {
        ["Code"] = code,
        ["Message"] = "provider-secret",
        ["RequestId"] = "safe-request-id"
    }));
}
