using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// Which transfers the resumer is allowed to take over. The predicate is the whole safety boundary of the sweep: a
/// worker that is still alive holds an unexpired lease, so matching one would mean two processes driving one transfer
/// against a destination — and a resumer holds no bytes, so it would settle a write that was about to succeed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArtifactTransferResumeClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ArtifactTransferState.Intended)]
    [InlineData(ArtifactTransferState.Uploading)]
    [InlineData(ArtifactTransferState.Uploaded)]
    [InlineData(ArtifactTransferState.Verifying)]
    [InlineData(ArtifactTransferState.RetryScheduled)]
    public void A_non_terminal_transfer_whose_lease_has_expired_is_abandoned(ArtifactTransferState state)
    {
        Matches(Intent(state, Now - TimeSpan.FromSeconds(1))).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ArtifactTransferState.Intended)]
    [InlineData(ArtifactTransferState.Uploading)]
    [InlineData(ArtifactTransferState.Uploaded)]
    [InlineData(ArtifactTransferState.Verifying)]
    [InlineData(ArtifactTransferState.RetryScheduled)]
    public void A_transfer_whose_lease_is_still_live_is_never_claimed(ArtifactTransferState state)
    {
        // The one property that keeps the sweep off a working writer. A live lease is renewed throughout the transfer,
        // so "still in the future" is the only evidence anyone has that the worker is still there.
        Matches(Intent(state, Now + TimeSpan.FromSeconds(1))).ShouldBeFalse();
    }

    [Fact]
    public void A_lease_that_expires_exactly_now_is_abandoned()
    {
        Matches(Intent(ArtifactTransferState.Uploading, Now)).ShouldBeTrue();
    }

    [Fact]
    public void A_transfer_nobody_ever_claimed_is_not_abandoned()
    {
        // A null lease is an intent no worker has taken, not one a worker dropped: the caller that minted it is about
        // to claim it with the bytes in hand. Taking it would settle a write that has not begun.
        Matches(Intent(ArtifactTransferState.Intended, null)).ShouldBeFalse();
    }

    [Fact]
    public void A_retry_scheduled_transfer_that_released_its_lease_is_out_of_reach()
    {
        // RetryScheduled has two halves and the state is in the predicate for the second one. The ORDINARY scheduled
        // retry released its lease when it was scheduled, and the caller still holding the bytes is the one entitled
        // to make that attempt — so a null lease keeps it out of reach here, exactly as for an unclaimed intent. What
        // the state earns its place for is the worker that died between CLAIMING a scheduled retry and transitioning
        // it onward: that row is parked in RetryScheduled still HOLDING its lease, abandoned on precisely the evidence
        // every other state is judged by, and reachable by nothing else. The expired-lease theory above pins it.
        Matches(Intent(ArtifactTransferState.RetryScheduled, null)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ArtifactTransferState.Committed)]
    [InlineData(ArtifactTransferState.Failed)]
    [InlineData(ArtifactTransferState.Cancelled)]
    public void A_terminal_transfer_is_never_abandoned(ArtifactTransferState state)
    {
        Matches(Intent(state, Now - TimeSpan.FromHours(1))).ShouldBeFalse();
    }

    private static bool Matches(ArtifactTransferIntent intent) =>
        ArtifactCasRuntimeCoordinator.Abandoned(Now).Compile().Invoke(intent);

    private static ArtifactTransferIntent Intent(ArtifactTransferState state, DateTimeOffset? leaseExpiresAt) => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), StorageProfileRevisionId = Guid.NewGuid(),
        IdempotencyKey = "resume-predicate", State = state, WorkerLeaseExpiresAt = leaseExpiresAt,
        WorkerFenceEpoch = leaseExpiresAt == null ? null : 1,
    };
}
