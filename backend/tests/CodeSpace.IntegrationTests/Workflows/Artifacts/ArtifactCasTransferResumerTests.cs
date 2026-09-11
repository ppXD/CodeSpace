using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Handlers.CommandHandlers.Storage;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The recovery loop the transfer saga never had. Every case here stages the same accident — a worker that claimed an
/// intent, touched the destination and never came back — and asks what the next sweep does with what it left behind.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class ArtifactCasTransferResumerTests
{
    private const int Batch = 200;

    /// <summary>The fence the staged worker took before it vanished. A sweep that claims one of these rows advances past it, so it is how a test tells "reached and claimed" from "never selected".</summary>
    private const long SeededFence = 1;

    private readonly PostgresFixture _fixture;

    public ArtifactCasTransferResumerTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_uploading_transfer_whose_worker_died_is_resumed_to_a_byte_correct_committed_placement()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-uploading-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes the dead worker already uploaded {scope}");
        var objectKey = ObjectKey(scope);
        storage.Objects[objectKey] = bytes;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading);

        await ResumeAsync(storage);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.Id == intentId);
        intent.State.ShouldBe(ArtifactTransferState.Committed, "the bytes were already at the destination, so the abandoned transfer had nothing left to do but be finished");
        intent.CompletedAt.ShouldNotBeNull();
        intent.WorkerLeaseExpiresAt.ShouldBeNull();
        intent.LastErrorCode.ShouldBeNull();

        var location = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.Id == intent.ArtifactLocationId);
        location.State.ShouldBe(ArtifactLocationState.Available);
        location.Revision.ShouldBe(1);
        location.StorageProfileRevisionId.ShouldBe(world.ProfileRevisionId);
        location.ObjectKey.ShouldBe(objectKey);
        location.ObservedSizeBytes.ShouldBe(bytes.LongLength);
        location.ProviderChecksumAlgorithm.ShouldBe("Sha256");
        location.ProviderChecksum.ShouldBe(SHA256.HashData(bytes));
        location.VerifiedAt.ShouldNotBeNull();

        var only = (await db.ArtifactLocationEvent.AsNoTracking().Where(value => value.ArtifactLocationId == location.Id).ToListAsync()).ShouldHaveSingleItem();
        only.Revision.ShouldBe(location.Revision);
        only.State.ShouldBe(location.State);
        only.ObservedSizeBytes.ShouldBe(location.ObservedSizeBytes);
        only.ProviderChecksum.ShouldBe(location.ProviderChecksum);
        only.ProviderChecksumAlgorithm.ShouldBe(location.ProviderChecksumAlgorithm);
        only.ProviderObjectVersion.ShouldBe(location.ProviderObjectVersion);
        only.ProviderETag.ShouldBe(location.ProviderETag);
        only.VerifiedAt.ShouldBe(location.VerifiedAt);

        var artifact = await db.ArtifactObject.AsNoTracking().SingleAsync(value => value.Id == intent.ArtifactObjectId);
        artifact.SizeBytes.ShouldBe(bytes.LongLength);
        artifact.Digest.ShouldBe(SHA256.HashData(bytes));
    }

    [Fact]
    public async Task A_transfer_whose_object_is_gone_from_the_destination_settles_terminally_instead_of_looping()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-missing-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes nothing ever stored {scope}");
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading);

        await ResumeAsync(storage);

        using var verify = _fixture.BeginScope();
        var intent = await verify.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.Id == intentId);
        intent.State.ShouldBe(ArtifactTransferState.Failed,
            "a resumer holds no content stream, so an object the destination does not have is unrecoverable for this intent however retryable the same answer is for a writer");
        intent.LastErrorCode.ShouldBe(nameof(ArtifactCasProblemCode.TargetMissing));
        intent.NextAttemptAt.ShouldBeNull("RetryScheduled releases the lease, which would park this intent exactly where the sweep cannot see it again");
        intent.RetryCount.ShouldBe(0);
        intent.CompletedAt.ShouldNotBeNull();
        intent.WorkerLeaseExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_settled_transfer_whose_bytes_are_still_on_the_destination_is_reported_as_unreachable()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-orphan-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"the content this transfer expected {scope}");
        var objectKey = ObjectKey(scope);
        storage.Objects[objectKey] = Encoding.UTF8.GetBytes("a shorter object that is not this content");
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading);

        var warnings = new RecordedWarnings();
        var summary = await ResumeAsync(storage, warnings);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.Id == intentId);
        intent.State.ShouldBe(ArtifactTransferState.Failed);
        intent.LastErrorCode.ShouldBe(nameof(ArtifactCasProblemCode.TargetCorrupt));
        intent.TargetObjectKey.ShouldBe(objectKey, "the settled intent is the durable record of where the unreachable bytes are");
        storage.Objects.ShouldContainKey(objectKey);
        (await db.ArtifactLocation.AsNoTracking().AnyAsync(value => value.StorageProfileRevisionId == world.ProfileRevisionId && value.ObjectKey == objectKey))
            .ShouldBeFalse("nothing names these bytes, which is exactly why the verifier, the placement reader and the retirement gate all miss them");
        summary.Orphaned.ShouldBeGreaterThanOrEqualTo(1, "a settled transfer whose object the destination still holds must be reported, not silently closed");

        // The counter above is a tally nobody reads at 3am. The warning is the only place an operator is handed the
        // coordinates of the bytes, so it is asserted rather than assumed — deleting it must fail this test.
        var reported = warnings.About(intentId).ShouldHaveSingleItem("the operator's one record of an object no artifact_location names");
        reported["TeamId"].ShouldBe(world.TeamId);
        reported["ObjectKey"].ShouldBe(objectKey);
        reported["ProfileRevisionId"].ShouldBe(world.ProfileRevisionId);
        reported["Problem"].ShouldBe(ArtifactCasProblemCode.TargetCorrupt);
    }

    /// <summary>
    /// A destination the team has disabled is a fact about the destination and about this minute, true of every
    /// abandoned transfer pointing at it — never an answer about THIS object, which may be sitting exactly where the
    /// intent says it is. So the pass records nothing on the transfer and settles nothing.
    ///
    /// <para>What it does write is its own claim, and that is the point of taking one first: the claim moves a
    /// transfer nobody could ask about to the BACK of the sweep's ordering rather than leaving it owning the front.
    /// The consequence is that the round trip cannot be closed by simply re-running the sweep here — this pass now
    /// holds a minutes-long lease, and 0131 admits no way to shorten a live one, which is exactly the refusal that
    /// keeps a live worker safe from this sweep. So re-selection is asserted against the predicate the sweep selects
    /// on, at BOTH sides of the lease instant; the half where a passed-over transfer is genuinely driven to a commit
    /// by a later pass is held by
    /// <see cref="A_destination_nobody_can_open_does_not_starve_the_transfers_queued_behind_it"/>.</para>
    /// </summary>
    [Fact]
    public async Task A_transfer_whose_destination_this_pod_cannot_open_is_parked_claimable_rather_than_burned()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-disabled-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes the dead worker already uploaded {scope}");
        var objectKey = ObjectKey(scope);
        storage.Objects[objectKey] = bytes;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading);
        await SetProfileStateAsync(world, StorageProfileState.Disabled);

        await ResumeAsync(storage);

        var parked = await IntentAsync(intentId);
        parked.State.ShouldBe(ArtifactTransferState.Uploading,
            "a destination that would not open said nothing about the object: the bytes may be exactly where the intent says they are, and Failed is a one-way door the database will not reopen");
        parked.CompletedAt.ShouldBeNull();
        parked.LastErrorCode.ShouldBeNull("nothing about the transfer was learned, so nothing about it was recorded");

        ShouldBeReselectedWhenItsClaimLapses(parked);
    }

    /// <summary>
    /// One destination nobody can open must not own the sweep.
    ///
    /// <para>The batch is bounded and ordered oldest-lease-first, so a transfer a pass refuses WITHOUT writing anything
    /// keeps its original place at the very front of the next pass, and the one after that. Enough of them on a single
    /// Disabled profile — one team's dead destination — and the resumer stops reaching any other transfer in the
    /// deployment for as long as that profile stays broken. This is the same shape as the "one dead destination must
    /// not consume the whole batch" defect the location verifier already carries a guard for.</para>
    ///
    /// <para>The fence claim is what buys the fairness, and it is the reason it is taken BEFORE the destination is
    /// asked anything: the claim advances the very lease the batch is ordered by, so a transfer this pass could not
    /// even put a question to drops to the BACK of the queue instead of the front — without settling anything, which
    /// is what would burn a transfer whose bytes may be sitting exactly where it says they are.</para>
    /// </summary>
    [Fact]
    public async Task A_destination_nobody_can_open_does_not_starve_the_transfers_queued_behind_it()
    {
        // Whatever earlier tests left abandoned would occupy the same bounded batch, and this test is entirely about
        // which rows a bounded batch reaches.
        await DrainAsync();

        var storage = new ResumeStorage();
        var blocked = await SeedWorldAsync();
        var healthy = await SeedWorldAsync();
        var blockers = new List<Guid>();

        for (var index = 0; index < ResumeAbandonedArtifactTransfersCommandHandler.BatchSize; index++)
        {
            var blockedScope = $"resume-blocker-{index}-{Guid.NewGuid():N}";
            blockers.Add(await StageAbandonedAsync(blocked, blockedScope, Encoding.UTF8.GetBytes(blockedScope), ArtifactTransferState.Uploading, leaseSeconds: 3));
        }

        await SetProfileStateAsync(blocked, StorageProfileState.Disabled);

        var scope = $"resume-behind-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes waiting behind a dead destination {scope}");
        storage.Objects[ObjectKey(scope)] = bytes;
        var behind = await StageAbandonedAsync(healthy, scope, bytes, ArtifactTransferState.Uploading, leaseSeconds: 8);

        // The longer offset puts this transfer LAST in the sweep's ordering, so a full batch of blockers is genuinely
        // in front of it and the first pass cannot reach it however the refusal is handled.
        await WaitForExpiredLeaseAsync(behind);

        await ResumeAsync(storage, batchSize: ResumeAbandonedArtifactTransfersCommandHandler.BatchSize);
        await ResumeAsync(storage, batchSize: ResumeAbandonedArtifactTransfersCommandHandler.BatchSize);

        (await IntentAsync(behind)).State.ShouldBe(ArtifactTransferState.Committed,
            "a full batch of transfers on one unopenable destination must not own the head of every pass: the claim each of them took moved it behind this one, so the second pass reaches the transfer the first could not");

        var parked = await IntentsAsync(blockers);
        parked.Count.ShouldBe(blockers.Count);
        parked.ShouldAllBe(intent => intent.WorkerFenceEpoch > SeededFence,
            "every blocker must genuinely have been reached and claimed by the first pass — otherwise nothing was in front of the transfer above and its commit proves nothing about starvation");
        parked.ShouldAllBe(intent => intent.State == ArtifactTransferState.Uploading,
            "the destination said nothing about any of these objects, so none of them may be settled — moving them off the head of the queue is the whole of what this pass is entitled to do");
        parked.ShouldAllBe(intent => intent.LastErrorCode == null && intent.CompletedAt == null);
    }

    /// <summary>
    /// The same line, but crossed AFTER the destination was opened and asked about the object. The disabled-profile
    /// case above is refused before any driver exists, so this is the only place a non-retryable code that is not
    /// about the object reaches the settle decision at all.
    ///
    /// <para>It stops at "still claimable" instead of driving the repaired transfer to a commit, because this pass DID
    /// take a fence: its own minutes-long lease is now what stands between the row and the next sweep, and 0131 admits
    /// no way to shorten a live one — that refusal is the whole reason a live worker is safe from this sweep. So the
    /// re-selection is asserted against the very predicate the sweep selects on rather than by waiting the lease out;
    /// the commit half of the round trip is pinned by
    /// <see cref="A_destination_nobody_can_open_does_not_starve_the_transfers_queued_behind_it"/>, which reaches a
    /// passed-over transfer through two real sweeps.</para>
    /// </summary>
    [Fact]
    public async Task A_destination_that_refuses_to_answer_for_the_object_leaves_the_transfer_claimable_rather_than_burning_it()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-forbidden-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes behind a permission that lapsed {scope}");
        var objectKey = ObjectKey(scope);
        storage.Objects[objectKey] = bytes;
        storage.ForbiddenObjectKey = objectKey;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading);

        await ResumeAsync(storage);

        var parked = await IntentAsync(intentId);
        parked.State.ShouldBe(ArtifactTransferState.Uploading,
            "a refusal to answer is not an answer: Forbidden is non-retryable for a writer, but it says only that this credential could not look — the object may be exactly where the intent says it is");
        parked.CompletedAt.ShouldBeNull();
        parked.LastErrorCode.ShouldBeNull("nothing about the object was learned, so nothing about it was recorded");

        ShouldBeReselectedWhenItsClaimLapses(parked);
    }

    [Fact]
    public async Task Two_resumers_racing_one_abandoned_transfer_commit_it_exactly_once()
    {
        var world = await SeedWorldAsync();
        // Settle whatever earlier suites left abandoned in this shared database, so both passes below reach THIS
        // test's intent together instead of one of them arriving after the other has already finished it.
        await ResumeAsync(new ResumeStorage());

        var storage = new ResumeStorage();
        var scope = $"resume-race-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"contended bytes {scope}");
        var objectKey = ObjectKey(scope);
        storage.Objects[objectKey] = bytes;
        storage.RendezvousObjectKey = objectKey;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Verifying);

        await Task.WhenAll(ResumeAsync(storage), ResumeAsync(storage));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.Id == intentId);
        intent.State.ShouldBe(ArtifactTransferState.Committed);

        var location = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.Id == intent.ArtifactLocationId);
        location.Revision.ShouldBe(1, "one commit wrote this placement; a second would have advanced it");
        (await db.ArtifactLocationEvent.AsNoTracking().CountAsync(value => value.ArtifactLocationId == location.Id)).ShouldBe(1);
        storage.Reads(objectKey).ShouldBe(1,
            "only the resumer that won the fenced claim may drive the transfer; the other must not read the destination on its behalf");
    }

    /// <summary>
    /// That a scheduled retry's wait is judged by the DATABASE's clock — the same one the sweep selects and orders by,
    /// and the same one every other clause of the claim statement is read against.
    ///
    /// <para>Both sides of the wait are driven on one row because only the pair says anything, and the skew is driven
    /// explicitly rather than waited for: this deployment is multi-worker and multi-node, so a pod whose wall clock
    /// disagrees with the database is an ordinary condition, not an accident. A pod running AHEAD must not jump a wait
    /// the database says is not over. A pod running BEHIND must not refuse a wait the database says IS over — because
    /// a claim that refuses writes NOTHING, and the batch is ordered by the very lease that claim would have advanced,
    /// so the row keeps the head of every following pass and starves the intents queued behind it.</para>
    /// </summary>
    [Fact]
    public async Task A_scheduled_retry_waits_and_is_taken_on_the_database_clock_whichever_way_this_pod_is_skewed()
    {
        // Whatever earlier tests left abandoned would share this bounded pass, and the window between the seeded lease
        // lapsing and the seeded backoff elapsing is what the first assertion below lives in.
        await DrainAsync();

        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-skew-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes the dead worker already uploaded {scope}");
        storage.Objects[ObjectKey(scope)] = bytes;
        var intentId = await SeedAbandonedRetryAsync(world, scope, bytes, backoffSeconds: 15);

        await ResumeAsync(storage, clock: new SkewedClock(TimeSpan.FromMinutes(15)));

        var waiting = await IntentAsync(intentId);
        waiting.State.ShouldBe(ArtifactTransferState.RetryScheduled,
            "the backoff had not elapsed by the clock the row was stamped by, so no pod may take this transfer however far ahead its own clock runs");
        waiting.WorkerFenceEpoch.ShouldBe(SeededFence, "a claim the wait refuses must not have written, so the fence it would have advanced is untouched");

        await WaitForElapsedBackoffAsync(intentId);
        await ResumeAsync(storage, clock: new SkewedClock(TimeSpan.FromMinutes(-15)));

        var resumed = await IntentAsync(intentId);
        resumed.State.ShouldBe(ArtifactTransferState.Committed,
            "the database says this transfer's wait is over and the sweep selected it on that same clock, so a pod running behind must not be the one clause that refuses it — that refusal writes nothing and parks the row at the head of every later pass");
        resumed.WorkerFenceEpoch.ShouldBe(SeededFence + 1);
    }

    /// <summary>
    /// The orphan a remote staged publish leaves when its worker is KILLED: bytes at a temporary key that no
    /// <c>artifact_location</c>, <c>artifact_object</c> or verifier can reach, because the driver's own cleanup lives
    /// in a <c>finally</c> and a <c>finally</c> does not run when the process dies.
    ///
    /// <para>Both rows are the same accident an instant apart, and both must end with the row naming nothing. Killed
    /// BEFORE the cleanup ran, the bytes are still there and this sweep is the only thing that will ever delete them.
    /// Killed AFTER it ran, they are already gone — and the record must still be cleared, or every ordinary write in
    /// the deployment leaves a phantom that each later pass re-asks the destination about forever.</para>
    ///
    /// <para>The final object is asserted ABSENT throughout. The reclaim's whole risk is deleting the wrong thing, and
    /// a transfer whose publish never happened is the case where the published key is empty and must stay so.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_killed_writers_staging_object_is_discarded_and_stops_being_named(bool bytesStillStaged)
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-staging-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes staged but never published {scope}");
        var staging = ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");
        if (bytesStillStaged) storage.Objects[staging] = bytes;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading, staging);

        var warnings = new RecordedWarnings();
        await ResumeAsync(storage, warnings);

        var swept = await IntentAsync(intentId);
        swept.TemporaryObjectKey.ShouldBeNull("the bytes are gone, so the row must stop naming them — a record that outlives its object is a phantom every later pass chases");
        storage.DiscardedStaging.ShouldContain(staging, "the sweep has to actually ask the destination; clearing the row without asking is how bytes become unreachable AND unbilled-for by nobody");
        storage.Objects.ShouldNotContainKey(staging, "a killed writer's staging object is the one thing this sweep exists to delete");
        storage.Objects.ShouldNotContainKey(ObjectKey(scope), "this transfer never published, so the destination key must still be empty — a reclaim may never invent or touch the final object");

        // The tally nobody reads at 3am is not the receipt. This warning is where an operator is handed the coordinates
        // of bytes that were being paid for and nothing named, so deleting it must fail this test.
        warnings.About(intentId).ShouldContain(record => Equals(record.GetValueOrDefault("TemporaryObjectKey"), staging));
        swept.State.ShouldBe(ArtifactTransferState.Failed, "reclaiming the staging object changes nothing about the transfer's own verdict: a resumer holds no bytes, so an unpublished object is still terminal for it");
        swept.LastErrorCode.ShouldBe(nameof(ArtifactCasProblemCode.TargetMissing));
    }

    /// <summary>
    /// A staging object whose worker is still alive is not an orphan, and nothing here may touch it. The fence is the
    /// evidence and the only evidence: a live lease means those bytes are mid-upload, and deleting them would break a
    /// write that was about to succeed.
    /// </summary>
    [Fact]
    public async Task A_live_workers_staging_object_is_left_exactly_where_it_is()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-staging-live-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes a live worker is still uploading {scope}");
        var staging = ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");
        storage.Objects[staging] = bytes;
        var intentId = await StageAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading, leaseSeconds: 120, staging);

        await ResumeAsync(storage);

        var untouched = await IntentAsync(intentId);
        untouched.WorkerFenceEpoch.ShouldBe(SeededFence, "a live lease is not claimable, so this row was never even selected");
        untouched.TemporaryObjectKey.ShouldBe(staging, "the record belongs to the worker that is still using it");
        storage.DiscardedStaging.ShouldNotContain(staging, "asking the destination to delete a live worker's staging object would break a write that is about to succeed");
        storage.Objects.ShouldContainKey(staging);
    }

    /// <summary>
    /// A sweep may only ever discard the temporary object its own ROW names. Everything else at the destination —
    /// another transfer's staging object included — is somebody else's, and a pass that swept the staging AREA rather
    /// than the recorded key would delete bytes a live writer is mid-upload with.
    ///
    /// <para>Doubles as the adoption path this change must not disturb: a worker killed after its publish but before
    /// its commit still gets driven to a committed placement, with nothing recorded to reclaim.</para>
    /// </summary>
    [Fact]
    public async Task A_transfer_that_recorded_no_temporary_object_leaves_every_other_object_alone()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-staging-none-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes the dead worker already published {scope}");
        var stranger = ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");
        storage.Objects[ObjectKey(scope)] = bytes;
        storage.Objects[stranger] = Encoding.UTF8.GetBytes("another transfer's staging object");
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading);

        await ResumeAsync(storage);

        (await IntentAsync(intentId)).State.ShouldBe(ArtifactTransferState.Committed, "the bytes were already published, so the abandoned transfer still had nothing left to do but be finished");
        storage.DiscardedStaging.ShouldNotContain(stranger, "the recorded key is the whole licence to delete; sweeping the staging area instead would destroy a live writer's upload");
        storage.Objects.ShouldContainKey(stranger);
    }

    /// <summary>
    /// Discard first, clear second — and a discard the destination refuses clears nothing. Clearing a record whose
    /// bytes are still there is how an orphan becomes one nobody will ever look for again — and here the same pass
    /// also settles the transfer TERMINALLY, which is what makes keeping it a PERMANENT orphan rather than one a
    /// later pass could still retry: 0226 gates the clearing write on a live lease and forces every terminal state to
    /// have released it, so a row that reaches Failed with the key still named can never have it cleared afterward.
    /// </summary>
    [Fact]
    public async Task A_staging_object_this_pass_could_not_discard_stays_named()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage { RefuseStagingDiscard = true };
        var scope = $"resume-staging-refused-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes the destination would not delete {scope}");
        var staging = ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");
        storage.Objects[staging] = bytes;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading, staging);

        await ResumeAsync(storage);

        var swept = await IntentAsync(intentId);
        swept.TemporaryObjectKey.ShouldBe(staging,
            "the bytes are still at the destination, and the row is the only record of where — dropping it here turns a reclaimable orphan into a permanent one");
        swept.State.ShouldBe(ArtifactTransferState.Failed,
            "this same pass also settled the transfer terminally, so the key above is not merely waiting for a later re-ask — a terminal row can never have it cleared again");
        storage.Objects.ShouldContainKey(staging);
    }

    /// <summary>
    /// What stops a wrong record from destroying a published object: the boundary is the DRIVER's, not the sweep's.
    /// A reclaim is handed a persisted value, so it may be stale, hand-edited or simply wrong — and the one value that
    /// must never be obeyed is the transfer's own destination key, since the staging area is a sibling of the object
    /// area rather than a parent of it. The sweep asks; the destination refuses; the placement survives.
    /// </summary>
    [Fact]
    public async Task A_record_naming_the_final_object_is_refused_by_the_destination_rather_than_obeyed()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"resume-staging-wrong-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"a published object a wrong record points at {scope}");
        var objectKey = ObjectKey(scope);
        storage.Objects[objectKey] = bytes;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading, objectKey);

        await ResumeAsync(storage);

        storage.DiscardedStaging.ShouldContain(objectKey, "the sweep does ask — putting the boundary in the driver is what makes a wrong record safe rather than trusting every caller of it");
        storage.Objects.ShouldContainKey(objectKey, "a key outside the driver's own staging area must be refused, or one stale record deletes a placement the ledger says is Available");
        var kept = await IntentAsync(intentId);
        kept.TemporaryObjectKey.ShouldBe(objectKey, "a refused discard clears nothing, so the wrong record stays visible to an operator instead of being quietly forgotten");
        kept.State.ShouldBe(ArtifactTransferState.Committed, "and the transfer itself is unaffected: its object is where it says it is");
    }

    /// <summary>
    /// That a refused transfer comes back to the sweep, and comes back on its LEASE rather than on its state alone.
    ///
    /// <para>Both sides of the instant are asserted because only the pair says anything. Evaluating the sweep's
    /// predicate at the row's own lease and expecting true cannot fail — the clause is <c>lease &lt;= now</c> — so it
    /// would still hold if the lease clause were dropped entirely and the sweep started stealing rows from live
    /// workers. The tick before the lease is the half that pins it.</para>
    /// </summary>
    private static void ShouldBeReselectedWhenItsClaimLapses(ArtifactTransferIntent parked)
    {
        parked.WorkerLeaseExpiresAt.ShouldNotBeNull("a claim that answered nothing must still leave a lapsing lease behind, or the sweep can never see this row again");

        var lease = parked.WorkerLeaseExpiresAt.Value;

        Selected(parked, lease - TimeSpan.FromTicks(1)).ShouldBeFalse(
            "while this pass's claim is still live the row is a WORKING transfer as far as the sweep can tell, and re-selecting it there would put two resumers on one transfer");
        Selected(parked, lease).ShouldBeTrue(
            "the moment that claim lapses the same sweep must take the row back; Failed would have been the one-way door instead");
    }

    /// <summary>What the sweep's own selection makes of this row at a given instant — the predicate <c>AbandonedAsync</c> queries with, so this is the sweep's WHERE clause and not a restatement of it.</summary>
    private static bool Selected(ArtifactTransferIntent intent, DateTimeOffset now) =>
        ArtifactCasRuntimeCoordinator.Abandoned(now).Compile().Invoke(intent);

    private async Task<ArtifactTransferResumeSummary> ResumeAsync(ResumeStorage storage, RecordedWarnings? warnings = null, int batchSize = Batch, TimeProvider? clock = null)
    {
        using var scope = Scope(storage, warnings, clock);

        return await scope.Resolve<IArtifactCasTransferResumer>().ResumeAbandonedAsync(batchSize, CancellationToken.None);
    }

    /// <summary>
    /// Clears whatever earlier classes in this shared database left abandoned, so the batch ordering the test below
    /// asserts on is its own. Each pass either settles a leftover terminally or advances its lease past the horizon,
    /// so the sweep converges on seeing nothing rather than being drained row by row.
    /// </summary>
    private async Task DrainAsync()
    {
        for (var pass = 0; pass < 5; pass++)
        {
            if ((await ResumeAsync(new ResumeStorage(), batchSize: 500)).Examined == 0) return;
        }

        throw new TimeoutException(
            "Abandoned transfers left by earlier tests still filled the sweep's batch after five draining passes, so this test's own ordering cannot be observed. "
            + "Inspect artifact_transfer_intent for non-terminal rows whose worker_lease_expires_at stays in the past across a pass.");
    }

    private ILifetimeScope Scope(ResumeStorage storage, RecordedWarnings? warnings, TimeProvider? clock = null) => _fixture.BeginScope(builder =>
    {
        builder.RegisterInstance(new ResumeFactoryCatalog(new ResumeStorageFactory(storage))).As<IArtifactStorageDriverFactoryCatalog>().SingleInstance();

        if (warnings != null) builder.RegisterInstance(warnings).As<ILogger<ArtifactCasRuntimeCoordinator>>().SingleInstance();
        if (clock != null) builder.RegisterInstance(clock).As<TimeProvider>().SingleInstance();
    });

    /// <summary>
    /// Stages the accident: an intent claimed by a worker that walked its saga forward and then vanished. The claim and
    /// every step are written the way the 0131 trigger demands — one revision each, a fence that only advances, a lease
    /// that is genuinely in the future — and the lease is then WAITED out rather than backdated, because the trigger
    /// refuses a lease that moves backwards and a backdated one would prove nothing about a live worker anyway.
    /// </summary>
    private async Task<Guid> SeedAbandonedAsync(World world, string scope, byte[] bytes, ArtifactTransferState state, string? temporaryObjectKey = null)
    {
        var intentId = await StageAbandonedAsync(world, scope, bytes, state, leaseSeconds: 3, temporaryObjectKey);

        (await LeaseExpiredAsync(intentId)).ShouldBeFalse("the seeded worker must still look alive here, or the wait below would prove nothing");
        await WaitForExpiredLeaseAsync(intentId);

        return intentId;
    }

    /// <summary>
    /// The same accident without the wait, so a batch of them can be staged at once and waited out together. The lease
    /// offset is what fixes each row's place in the sweep's oldest-first ordering: a row staged with a SHORTER offset
    /// than another lapses earlier and is therefore selected ahead of it, whichever order the two were inserted in.
    /// </summary>
    private async Task<Guid> StageAbandonedAsync(World world, string scope, byte[] bytes, ArtifactTransferState state, int leaseSeconds, string? temporaryObjectKey = null)
    {
        var intentId = Guid.NewGuid();
        var objectKey = ObjectKey(scope);
        using var seed = _fixture.BeginScope();
        var db = seed.Resolve<CodeSpaceDbContext>();

        // temporary_object_key is written at INSERT because it is the only place the seeding CAN write it: the claim
        // below is a fence advance, which 0131 makes claim-only, and the ladder's steps are saga transitions that this
        // seeding keeps to one field each. A killed writer's row looks exactly like this either way — claimed, part
        // way up the ladder, naming the temporary object its upload was occupying.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO artifact_transfer_intent (
                id, team_id, storage_profile_revision_id, idempotency_key, expected_digest_algorithm, expected_digest,
                expected_size_bytes, target_locator, target_object_key, temporary_object_key, state, revision, retry_count,
                created_date, created_by, last_modified_date, last_modified_by)
            VALUES ({intentId}, {world.TeamId}, {world.ProfileRevisionId}, {scope}, 'Sha256', {SHA256.HashData(bytes)},
                {bytes.LongLength}, {objectKey}, {objectKey}, NULLIF({temporaryObjectKey ?? string.Empty}, ''), 'Intended', 1, 0,
                clock_timestamp(), {world.ActorId}, clock_timestamp(), {world.ActorId})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE artifact_transfer_intent
            SET worker_fence_epoch = {SeededFence}, worker_lease_expires_at = clock_timestamp() + ({leaseSeconds} * INTERVAL '1 second'),
                revision = revision + 1, last_modified_date = clock_timestamp()
            WHERE id = {intentId}
            """);
        foreach (var step in Ladder(state))
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE artifact_transfer_intent SET state = {step}, revision = revision + 1, last_modified_date = clock_timestamp() WHERE id = {intentId}
                """);

        return intentId;
    }

    /// <summary>
    /// The same accident parked in <c>RetryScheduled</c>: a worker that CLAIMED a scheduled retry and died before it
    /// transitioned the intent onward, so the row is still holding the lease it took — the one shape of that state the
    /// sweep can reach at all. The backoff is written relative to <c>clock_timestamp()</c> because that is the clock
    /// the claim judges it by, and every timestamp this seeding writes is read from it.
    /// </summary>
    private async Task<Guid> SeedAbandonedRetryAsync(World world, string scope, byte[] bytes, int backoffSeconds)
    {
        var intentId = await StageAbandonedAsync(world, scope, bytes, ArtifactTransferState.Intended, leaseSeconds: 3);

        using var seed = _fixture.BeginScope();
        await seed.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE artifact_transfer_intent
            SET state = 'RetryScheduled', retry_count = retry_count + 1, last_error_code = 'ProviderTimeout',
                next_attempt_at = clock_timestamp() + ({backoffSeconds} * INTERVAL '1 second'),
                revision = revision + 1, last_modified_date = clock_timestamp()
            WHERE id = {intentId}
            """);

        await WaitForExpiredLeaseAsync(intentId);

        return intentId;
    }

    private static IEnumerable<string> Ladder(ArtifactTransferState state) => state switch
    {
        ArtifactTransferState.Uploading => ["Uploading"],
        ArtifactTransferState.Uploaded => ["Uploading", "Uploaded"],
        ArtifactTransferState.Verifying => ["Uploading", "Uploaded", "Verifying"],
        _ => [],
    };

    private async Task WaitForExpiredLeaseAsync(Guid intentId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await LeaseExpiredAsync(intentId)) return;

            await Task.Delay(100);
        }

        throw new TimeoutException($"The seeded lease on intent {intentId} never lapsed within 20s, so the resumer would never see it as abandoned. Check worker_lease_expires_at against clock_timestamp() for that row.");
    }

    private async Task<bool> LeaseExpiredAsync(Guid intentId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Database
            .SqlQuery<bool>($"SELECT (worker_lease_expires_at <= clock_timestamp()) AS \"Value\" FROM artifact_transfer_intent WHERE id = {intentId}")
            .SingleAsync();
    }

    private async Task WaitForElapsedBackoffAsync(Guid intentId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(40);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await BackoffElapsedAsync(intentId)) return;

            await Task.Delay(100);
        }

        throw new TimeoutException($"The seeded backoff on intent {intentId} never elapsed within 40s, so a claim refusing it would be refusing it for the right reason and would prove nothing. Check next_attempt_at against clock_timestamp() for that row.");
    }

    private async Task<bool> BackoffElapsedAsync(Guid intentId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Database
            .SqlQuery<bool>($"SELECT (next_attempt_at <= clock_timestamp()) AS \"Value\" FROM artifact_transfer_intent WHERE id = {intentId}")
            .SingleAsync();
    }

    private async Task<ArtifactTransferIntent> IntentAsync(Guid intentId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.Id == intentId);
    }

    private async Task<IReadOnlyList<ArtifactTransferIntent>> IntentsAsync(IReadOnlyCollection<Guid> intentIds)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking().Where(value => intentIds.Contains(value.Id)).ToListAsync();
    }

    /// <summary>Breaks (and later repairs) the destination the way an operator does: the profile itself, leaving the revision the intent pins untouched.</summary>
    private async Task SetProfileStateAsync(World world, StorageProfileState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.SingleAsync(value => value.Id == world.ProfileId);
        profile.State = state;
        await db.SaveChangesAsync();
    }

    private static string ObjectKey(string scope) => $"cas/{scope}.bin";

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profileRevisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"cas-resume-{actorId:N}@test.local", Name = $"cas-resume-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"cas-resume-{teamId:N}", Name = "CAS Resume Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"resume-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = profileRevisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{\"rootPath\":\"/unused/fake\"}",
            NamespaceFingerprint = $"sha256:{new string('a', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();

        return new World(teamId, actorId, profileId, profileRevisionId);
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId, Guid ProfileRevisionId);

    /// <summary>A pod whose wall clock sits a fixed offset away from the database's — the ordinary condition on a multi-node deployment, driven explicitly here rather than waited on.</summary>
    private sealed class SkewedClock(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + skew;
    }

    /// <summary>
    /// The sweep's warnings, kept as their structured properties rather than a rendered string, so what an operator is
    /// actually handed — which intent, which team, which object, which profile revision — is the thing under test.
    /// </summary>
    private sealed class RecordedWarnings : ILogger<ArtifactCasRuntimeCoordinator>
    {
        private readonly ConcurrentBag<IReadOnlyDictionary<string, object?>> _records = [];

        /// <summary>The warnings naming one intent. The sweep is deployment-wide, so whatever else it found in this shared database is not this test's business.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> About(Guid intentId) =>
            _records.Where(record => Equals(record.GetValueOrDefault("IntentId"), intentId)).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning || state is not IReadOnlyList<KeyValuePair<string, object?>> properties) return;

            _records.Add(properties.ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal));
        }
    }

    private sealed class ResumeFactoryCatalog(IArtifactStorageDriverFactory factory) : IArtifactStorageDriverFactoryCatalog
    {
        public IArtifactStorageDriverFactory? Get(string providerTypeKey) => string.Equals(providerTypeKey, factory.ProviderTypeKey, StringComparison.Ordinal) ? factory : null;
        public IArtifactStorageDriverFactory Require(string providerTypeKey) => Get(providerTypeKey) ?? throw new NotSupportedException();
    }

    private sealed class ResumeStorageFactory(ResumeStorage storage) : IArtifactStorageDriverFactory
    {
        public string ProviderTypeKey => LocalRwxArtifactStorageDriverFactory.TypeKey;
        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IArtifactStorageDriver>(new ResumeStorageDriver(storage));
    }

    /// <summary>One destination shared by every driver a pass opens, so two concurrent passes see the same objects and the same call counts.</summary>
    private sealed class ResumeStorage
    {
        /// <summary>Where this destination stages, a SIBLING of the object namespace exactly as a remote driver's reserved area is — so a key from one can never name an object in the other.</summary>
        public const string StagingArea = "resume-staging/";

        private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _rendezvous = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Every object this destination ever PUBLISHED, the dead worker's own server-side copy included. A resumer
        /// holds no bytes, so the only correct value after a recovery is the one the crash left behind — which is why
        /// the pre-crash copy is recorded here rather than the final object simply being planted.
        /// </summary>
        public ConcurrentBag<string> Publishes { get; } = [];

        /// <summary>The copy the killed worker had already finished: the bytes at the final key, and the publish that put them there on the record.</summary>
        public void Publish(string objectKey, byte[] bytes)
        {
            Objects[objectKey] = bytes;
            Publishes.Add(objectKey);
        }

        /// <summary>Every key a reclaim was ATTEMPTED for, refusals included — so a test can tell "the sweep did not ask" from "the sweep asked and was told no".</summary>
        public ConcurrentBag<string> DiscardedStaging { get; } = [];

        /// <summary>A destination that will not delete right now. Retryable, because a discard that cannot happen is a fact about this minute and never about the bytes.</summary>
        public bool RefuseStagingDiscard { get; set; }

        /// <summary>The key whose readers wait for each other, so a second resumer that should never arrive is given every chance to.</summary>
        public string? RendezvousObjectKey { get; set; }

        /// <summary>The key this destination refuses to answer for at all — a credential that lost its read grant while the worker was gone.</summary>
        public string? ForbiddenObjectKey { get; set; }

        public int Reads(string objectKey) => _reads.GetValueOrDefault(objectKey);

        public async Task ReadAsync(string objectKey)
        {
            _reads.AddOrUpdate(objectKey, 1, (_, count) => count + 1);
            if (!string.Equals(objectKey, RendezvousObjectKey, StringComparison.Ordinal)) return;

            if (Interlocked.Increment(ref _arrivals) >= 2) _rendezvous.TrySetResult();
            await Task.WhenAny(_rendezvous.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    /// <summary>A destination that publishes through a temporary object, which is the only kind that can leave a staging orphan behind for this sweep to find.</summary>
    private sealed class ResumeStorageDriver(ResumeStorage storage) : IArtifactStorageDriver, IArtifactStorageStagingReclaimer
    {
        public StorageProviderCapabilities Capabilities =>
            StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.ConditionalCreate;

        public string MintStagingObjectKey() => ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");

        /// <summary>Absent is success, as the reclaim contract requires — and a key outside the staging area is refused, because that is the only thing standing between a stale record and a published object.</summary>
        public ValueTask<ArtifactStorageDeleteResult> DiscardStagingAsync(string stagingObjectKey, CancellationToken cancellationToken)
        {
            storage.DiscardedStaging.Add(stagingObjectKey);
            if (!stagingObjectKey.StartsWith(ResumeStorage.StagingArea, StringComparison.Ordinal))
                return ValueTask.FromResult(ArtifactStorageDeleteResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.InvalidRequest, "not this driver's staging area")));
            if (storage.RefuseStagingDiscard)
                return ValueTask.FromResult(ArtifactStorageDeleteResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Unavailable, "destination refused the discard", IsRetryable: true)));

            storage.Objects.TryRemove(stagingObjectKey, out _);

            return ValueTask.FromResult(ArtifactStorageDeleteResult.Removed());
        }

        /// <summary>Recorded before it throws, so a recovery that re-published is a COUNT a test can assert on rather than an exception the sweep's own recoverable-fault arm would swallow into an inconclusive pass.</summary>
        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken)
        {
            storage.Publishes.Add(request.ObjectKey);

            throw new InvalidOperationException("The resumer holds no content stream and must never upload.");
        }

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.ObjectKey, storage.ForbiddenObjectKey, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("this credential may not look at that key");

            return ValueTask.FromResult(storage.Objects.TryGetValue(request.ObjectKey, out var bytes)
                ? ArtifactStorageHeadResult.Found(Metadata(request.ObjectKey, bytes))
                : ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing")));
        }

        public async ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            if (!storage.Objects.TryGetValue(request.ObjectKey, out var stored))
                return ArtifactStorageReadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing"));

            await storage.ReadAsync(request.ObjectKey);
            var metadata = Metadata(request.ObjectKey, stored);
            if ((request.ExpectedETag != null && !string.Equals(request.ExpectedETag, metadata.ETag, StringComparison.Ordinal))
                || (request.ExpectedVersion != null && !string.Equals(request.ExpectedVersion, metadata.Version, StringComparison.Ordinal)))
                return ArtifactStorageReadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.ConditionNotMet, "condition"));

            return ArtifactStorageReadResult.Opened(new MemoryStream(stored, writable: false), stored.LongLength, stored.LongLength, metadata);
        }

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(storage.Objects.TryRemove(request.ObjectKey, out _)
                ? ArtifactStorageDeleteResult.Removed()
                : ArtifactStorageDeleteResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing")));

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Available, Latency = TimeSpan.Zero });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ArtifactStorageObjectMetadata Metadata(string key, byte[] bytes)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

            return new ArtifactStorageObjectMetadata { ObjectKey = key, Length = bytes.LongLength, Sha256 = sha, ETag = $"etag-{sha}", Version = "v1" };
        }
    }
}
