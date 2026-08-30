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
    [InlineData(ArtifactStorageErrorCode.Missing, false, true, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Conclusive)]
    [InlineData(ArtifactStorageErrorCode.Missing, false, false, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Uncorroborated)]      // an unmounted volume: File.Exists is false for a deleted object AND for a gone mount
    [InlineData(ArtifactStorageErrorCode.Unauthorized, false, false, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Uncorroborated)] // a credential that lost its permission refuses every key alike
    [InlineData(ArtifactStorageErrorCode.Forbidden, false, false, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Uncorroborated)]
    [InlineData(ArtifactStorageErrorCode.Unavailable, false, false, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Uncorroborated)]
    [InlineData(ArtifactStorageErrorCode.Unavailable, false, true, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Conclusive)]
    [InlineData(ArtifactStorageErrorCode.Throttled, true, true, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Inconclusive)]        // corroboration cannot promote an answer that was never about the destination
    [InlineData(ArtifactStorageErrorCode.ConditionNotMet, false, true, ArtifactCasRuntimeCoordinator.AbandonmentEvidence.Inconclusive)]
    public void An_answer_that_settles_closes_nothing_until_the_destination_answers_for_itself(ArtifactStorageErrorCode code, bool retryable, bool destinationAnswers, ArtifactCasRuntimeCoordinator.AbandonmentEvidence expected) =>
        ArtifactCasRuntimeCoordinator.Weigh(new ArtifactStorageError(code, "answer", retryable), destinationAnswers).ShouldBe(expected);

    [Theory]
    [InlineData(ArtifactStorageProbeStatus.Available, null, false, true)]                                     // a destination that is simply there
    [InlineData(ArtifactStorageProbeStatus.ReadOnly, ArtifactStorageErrorCode.Forbidden, false, true)]         // it can be read; it just cannot be written, which this never needs
    [InlineData(ArtifactStorageProbeStatus.Unavailable, ArtifactStorageErrorCode.Unavailable, false, true)]    // NoSuchBucket ABOUT ITSELF: gone for good is an answer, and the one this operation exists for
    [InlineData(ArtifactStorageProbeStatus.Unavailable, ArtifactStorageErrorCode.Unavailable, true, false)]    // an unmounted volume: the same code, retryable, because mounting it back is a thing that happens
    [InlineData(ArtifactStorageProbeStatus.Unavailable, ArtifactStorageErrorCode.Unauthorized, false, false)]  // a rotated key: durable, and about the CREDENTIAL — it never mentions whether anything is behind it
    [InlineData(ArtifactStorageProbeStatus.Unavailable, ArtifactStorageErrorCode.Forbidden, false, false)]     // a permission taken away, which is indistinguishable from a namespace you can no longer see
    [InlineData(ArtifactStorageProbeStatus.Unavailable, ArtifactStorageErrorCode.Missing, false, false)]       // the contract's word for an OBJECT that is not there, and a probe names no object
    [InlineData(ArtifactStorageProbeStatus.Degraded, ArtifactStorageErrorCode.ProviderFailure, true, false)]   // having a bad minute answers nothing about anything
    [InlineData(ArtifactStorageProbeStatus.Unavailable, null, false, false)]                                  // not there, and it did not say why — no answer to weigh
    public void A_probe_answers_for_the_destination_when_it_is_reachable_or_durably_gone(ArtifactStorageProbeStatus status, ArtifactStorageErrorCode? code, bool retryable, bool answers) =>
        ArtifactCasRuntimeCoordinator.AnswersForItself(new ArtifactStorageProbeResult
        {
            Status = status, Latency = TimeSpan.Zero,
            Error = code is { } value ? new ArtifactStorageError(value, "answer", retryable) : null,
        }).ShouldBe(answers);

    [Fact]
    public void A_probe_that_produced_no_result_at_all_answers_nothing() =>
        ArtifactCasRuntimeCoordinator.AnswersForItself(null).ShouldBeFalse();
}
