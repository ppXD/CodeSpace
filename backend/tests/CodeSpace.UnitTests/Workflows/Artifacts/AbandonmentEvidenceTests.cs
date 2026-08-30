using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

/// <summary>
/// What counts as proof that a destination can no longer serve a placement.
///
/// <para>Abandonment closes the record without deleting anything, so its entire safety is this predicate: an answer
/// about the MOMENT — a timeout, a 5xx, a throttle, a resolution blip — must never close the record of bytes it
/// could not testify about. The trap is that <c>Unavailable</c> is two answers wearing one code: a deleted bucket
/// and a transient fault both classify to it, told apart only by retryability.</para>
/// </summary>
public sealed class AbandonmentEvidenceTests
{
    [Theory]
    [InlineData(ArtifactStorageErrorCode.Missing, false, true)]        // the object is not there — a statement about the object
    [InlineData(ArtifactStorageErrorCode.Unauthorized, false, true)]   // the key is refused — the revoked-credential exit
    [InlineData(ArtifactStorageErrorCode.Forbidden, false, true)]      // ditto, without the credential attribution
    [InlineData(ArtifactStorageErrorCode.Unavailable, false, true)]    // NoSuchBucket: gone for good, marked non-retryable by the classifier
    [InlineData(ArtifactStorageErrorCode.Unavailable, true, false)]    // a 5xx or network fault: the SAME code, having a bad minute
    [InlineData(ArtifactStorageErrorCode.Throttled, true, false)]      // refusing the pace, not the object
    [InlineData(ArtifactStorageErrorCode.ProviderFailure, true, false)]
    [InlineData(ArtifactStorageErrorCode.ConditionNotMet, false, false)] // an answer about the REQUEST, never about the destination
    public void Only_a_durable_answer_about_the_destination_settles(ArtifactStorageErrorCode code, bool retryable, bool settles) =>
        ArtifactCasRuntimeCoordinator.Settles(new ArtifactStorageError(code, "answer", retryable)).ShouldBe(settles);

    [Theory]
    [InlineData(ArtifactCasProblemCode.CredentialUnavailable, false, true)]  // the credential row is revoked — the designed exit
    [InlineData(ArtifactCasProblemCode.ProfileRevisionMissing, false, true)] // the destination's config no longer resolves
    [InlineData(ArtifactCasProblemCode.ProviderTimeout, true, false)]        // the broker had a bad second — the record outlives it
    [InlineData(ArtifactCasProblemCode.CredentialBrokerUnavailable, true, false)]
    public void A_failure_to_open_the_destination_settles_only_when_it_is_durable(ArtifactCasProblemCode code, bool retryable, bool settles) =>
        ArtifactCasRuntimeCoordinator.Settles(new ArtifactCasProblem(code, retryable)).ShouldBe(settles);
}
