using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// The crash matrix of a staged publish, walked step by step: stage, verify the staged bytes, server-side copy, record
/// the placement. Every case here kills the writer at ONE of those instants and asks the recovery sweep for exactly
/// one committed placement — or for an honest refusal, when one placement is not something it may manufacture.
///
/// <para>The crash is STAGED rather than injected, and that is a choice about determinism. What a killed process
/// leaves behind is entirely a row plus what is at the destination: no <c>finally</c> ran, no fault propagated, no
/// decorator observed it. Seeding those two directly reproduces that with no race and no child process, whereas a
/// fault-injecting driver would also have to prove its fault is indistinguishable from a kill — which it is not, since
/// a thrown fault runs the driver's own cleanup and a kill does not.</para>
/// </summary>
public sealed partial class ArtifactCasTransferResumerTests
{
    /// <summary>
    /// The worker was killed after its server-side COPY: the object is at the final key, and nothing in the ledger
    /// says so. The only correct recovery is to finish the transfer off the bytes that are already there.
    ///
    /// <para>Two things it must not do, and neither is visible from the intent's own state. It must not RE-PUBLISH —
    /// it holds no content stream, so a second copy could only invent one — which is why the destination counts the
    /// dead worker's copy and the count is asserted unmoved rather than merely "no error occurred". And it must never
    /// DELETE the final object: the staging reclaim runs on this same path and is handed a persisted key, so the one
    /// object a recovery may not touch is the one it is trying to commit.</para>
    ///
    /// <para>The theory's rows are the crash instants between the copy and the commit, which is where the saga spends
    /// its post-copy life: mid-upload as far as the ledger knows, uploaded, or already verifying. All three carry a
    /// staging record too, because a worker killed before its own <c>finally</c> ran leaves one — so each row is also
    /// the case where the cleanup and the commit have to happen in the SAME pass.</para>
    /// </summary>
    [Theory]
    [InlineData(ArtifactTransferState.Uploading)]
    [InlineData(ArtifactTransferState.Uploaded)]
    [InlineData(ArtifactTransferState.Verifying)]
    public async Task A_transfer_killed_after_its_copy_commits_without_re_copying_and_never_deletes_the_final_object(ArtifactTransferState crashedAt)
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"crash-after-copy-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes the dead worker had already copied into place {scope}");
        var objectKey = ObjectKey(scope);
        var staging = ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");
        storage.Publish(objectKey, bytes);
        storage.Objects[staging] = bytes;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, crashedAt, staging);

        await ResumeAsync(storage);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.Id == intentId);
        intent.State.ShouldBe(ArtifactTransferState.Committed, "the copy had already happened, so the only thing left undone was the record of it");
        intent.TemporaryObjectKey.ShouldBeNull("the cleanup and the commit are the same pass's work; a terminal row's staging record can never be cleared afterwards");

        storage.Publishes.Count(key => key == objectKey).ShouldBe(1,
            "the dead worker's copy is the only one there may ever be — a resumer holds no content stream, so a second publish could only be writing bytes it invented");
        storage.Objects.ShouldContainKey(objectKey, "the object under commit is the one thing on this path a recovery may never delete, staging reclaim and all");
        storage.Objects[objectKey].ShouldBe(bytes);
        storage.Objects.ShouldNotContainKey(staging, "the staging bytes the killed writer's own finally never discarded are exactly what this sweep exists to remove");

        var location = await db.ArtifactLocation.AsNoTracking()
            .SingleAsync(value => value.StorageProfileRevisionId == world.ProfileRevisionId && value.ObjectKey == objectKey);
        location.Id.ShouldBe(intent.ArtifactLocationId!.Value);
        location.State.ShouldBe(ArtifactLocationState.Available);
        location.Revision.ShouldBe(1, "one commit wrote this placement; a second pass over the same crash would have advanced it");
        location.ObservedSizeBytes.ShouldBe(bytes.LongLength);
        location.ProviderChecksum.ShouldBe(SHA256.HashData(bytes));
        (await db.ArtifactLocationEvent.AsNoTracking().CountAsync(value => value.ArtifactLocationId == location.Id)).ShouldBe(1);
    }

    /// <summary>
    /// The worker was killed in the gap between verifying its staged bytes and copying them to the final key, and the
    /// staged object was corrupted meanwhile. Nothing is at the final key, and the recovery must keep it that way.
    ///
    /// <para>The staged bytes are the trap. They are the only bytes anywhere for this transfer, they are sitting at a
    /// key the row itself names, and promoting them is a single copy away — which is why a resumer is given the
    /// reclaim capability and NOT the publish one. A verification that happened before the corruption proves nothing
    /// about what is there now: the gap is exactly where a truncated upload, a re-used key or a hostile overwrite
    /// lands, and there is no re-verify on this path because a resumer cannot re-read what it never streamed.</para>
    ///
    /// <para>So the answer must be a typed refusal, the final key must stay empty, and the corrupted staging object
    /// must be deleted rather than left to be found and trusted by something later.</para>
    /// </summary>
    [Fact]
    public async Task A_transfer_killed_between_its_verify_and_its_copy_refuses_the_placement_and_promotes_no_staged_bytes()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"crash-before-copy-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"the content this transfer verified before it died {scope}");
        var objectKey = ObjectKey(scope);
        var staging = ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");
        storage.Objects[staging] = Encoding.UTF8.GetBytes("corrupted in the gap: a shorter object at the same staging key");
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Uploading, staging);

        var summary = await ResumeAsync(storage);

        var refused = await IntentAsync(intentId);
        refused.State.ShouldBe(ArtifactTransferState.Failed, "a resumer holds no content stream, so an object the final key does not have is unrecoverable for this intent");
        refused.LastErrorCode.ShouldBe(nameof(ArtifactCasProblemCode.TargetMissing), "the refusal has to be typed; a pass that merely declined to commit leaves an intent nobody can act on");
        refused.CompletedAt.ShouldNotBeNull();

        storage.Objects.ShouldNotContainKey(objectKey,
            "the staged bytes are one copy away from the final key and are no longer this transfer's content — a resumer that could publish them would publish corruption under a content-addressed key");
        storage.Publishes.Count(key => key == objectKey).ShouldBe(0, "there was no copy before the crash and there may be none after it");
        storage.Objects.ShouldNotContainKey(staging, "bytes that are demonstrably not this content must not be left at a key the destination bills for and something later may trust");
        refused.TemporaryObjectKey.ShouldBeNull();

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .AnyAsync(value => value.StorageProfileRevisionId == world.ProfileRevisionId && value.ObjectKey == objectKey))
            .ShouldBeFalse("no placement may name a key the destination is not holding this content at");
        summary.Committed.ShouldBe(0, "and the pass must not report a success it did not have");
    }

    /// <summary>
    /// Recovery arriving at a transfer whose placement is already recorded. There is nothing left to place, so the
    /// pass owes exactly one thing — the cleanup — and must add nothing to the ledger or the destination.
    ///
    /// <para>Why the crash instant "after the record, before the cleanup" needs no recovery of its own: the sweep does
    /// the cleanup FIRST and the commit second, deliberately. 0226 admits the clearing write only from a fence holder
    /// on a LIVE lease, and every terminal state must release its lease — so a pass that committed before clearing
    /// would find the column permanently out of reach. Both halves land in one pass or the record outlives its object
    /// forever, which is what the first assertion below is really pinning.</para>
    ///
    /// <para>The second pass is the other half. Recovery is deployment-wide, bounded and repeated, so it runs again
    /// over the same world every few minutes for as long as the deployment lives; "exactly once" is a claim about all
    /// of those passes and not just the one that did the work.</para>
    /// </summary>
    [Fact]
    public async Task A_recovery_that_finds_its_placement_already_recorded_does_the_cleanup_only_and_adds_nothing()
    {
        var world = await SeedWorldAsync();
        var storage = new ResumeStorage();
        var scope = $"crash-after-record-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes($"bytes already copied and already recorded {scope}");
        var objectKey = ObjectKey(scope);
        var staging = ResumeStorage.StagingArea + Guid.NewGuid().ToString("N");
        storage.Publish(objectKey, bytes);
        storage.Objects[staging] = bytes;
        var intentId = await SeedAbandonedAsync(world, scope, bytes, ArtifactTransferState.Verifying, staging);

        await ResumeAsync(storage);
        var recorded = await PlacementAsync(world, objectKey);
        var committed = await IntentAsync(intentId);
        committed.State.ShouldBe(ArtifactTransferState.Committed);
        committed.TemporaryObjectKey.ShouldBeNull(
            "the clearing write is admitted only while the lease is live, and committing releases it — so a pass that recorded the placement before doing the cleanup could never do the cleanup at all");
        storage.Objects.ShouldNotContainKey(staging);

        await ResumeAsync(storage);

        var again = await PlacementAsync(world, objectKey);
        again.Id.ShouldBe(recorded.Id, "a later pass over a placement that already exists must not mint a second one");
        again.Revision.ShouldBe(recorded.Revision, "and must not write an observation onto it either — it read nothing and proved nothing");
        again.VerifiedAt.ShouldBe(recorded.VerifiedAt);
        (await IntentAsync(intentId)).Revision.ShouldBe(committed.Revision, "a settled transfer is not a transfer; a pass that claimed it would be claiming a terminal row");
        storage.Publishes.Count(key => key == objectKey).ShouldBe(1, "the dead worker's copy, and no pass after it, is what put those bytes there");
        storage.DiscardedStaging.Count(key => key == staging).ShouldBe(1, "the cleanup is owed once; re-asking a destination about a key nothing names any more is a request nobody can act on");
        storage.Objects[objectKey].ShouldBe(bytes, "and the object itself is untouched by every pass that arrives after it was placed");
    }

    /// <summary>The one placement that may exist for a destination's object key. Read by key rather than by the intent's own id, so a second row anywhere under it fails here instead of hiding behind whichever id was asked for.</summary>
    private async Task<ArtifactLocation> PlacementAsync(World world, string objectKey)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .SingleAsync(value => value.StorageProfileRevisionId == world.ProfileRevisionId && value.ObjectKey == objectKey);
    }
}
