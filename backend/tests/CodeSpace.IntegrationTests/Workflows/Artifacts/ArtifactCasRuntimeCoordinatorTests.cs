using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactCasRuntimeCoordinatorTests
{
    private readonly PostgresFixture _fixture;

    public ArtifactCasRuntimeCoordinatorTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Routed_purge_claims_the_exact_recorded_location_before_provider_io_and_finalizes_it_monotonically()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState
        {
            Capabilities = StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead
                | StorageProviderCapabilities.ConditionalCreate | StorageProviderCapabilities.Delete,
            BlockNextDelete = true,
        };
        var bytes = "routed bytes to reclaim"u8.ToArray();
        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-order")))
            .ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        using var purgeScope = Scope(storage);
        var pending = purgeScope.Resolve<IArtifactCasPurgeCoordinator>().PurgeAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId, ActorId = world.ActorId,
        }, CancellationToken.None);
        var first = await Task.WhenAny(storage.DeleteEntered.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
        var early = first == pending ? await pending : null;
        first.ShouldBe(storage.DeleteEntered.Task, $"provider delete must be reached, but purge completed early as {early}");

        using (var observe = _fixture.BeginScope())
        {
            var location = await observe.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
                .SingleAsync(value => value.Id == committed.ArtifactLocationId);
            location.State.ShouldBe(ArtifactLocationState.Deleting, "the durable claim must commit before provider bytes are touched");
            location.Revision.ShouldBe(2);
        }

        storage.ReleaseDelete.TrySetResult();
        var purged = (await pending).ShouldBeOfType<ArtifactCasPurgeResult.Purged>();
        purged.LocationId.ShouldBe(committed.ArtifactLocationId);
        purged.LocationRevision.ShouldBe(3);
        purged.WasAlreadyPurged.ShouldBeFalse();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var finalized = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.Id == committed.ArtifactLocationId);
        finalized.State.ShouldBe(ArtifactLocationState.Purged);
        finalized.Revision.ShouldBe(3);
        (await db.ArtifactLocationEvent.AsNoTracking().Where(value => value.ArtifactLocationId == finalized.Id)
            .OrderBy(value => value.Revision).Select(value => value.State).ToListAsync())
            .ShouldBe(new[] { ArtifactLocationState.Available, ArtifactLocationState.Deleting, ArtifactLocationState.Purged });
        storage.Objects.ShouldNotContainKey("cas/purge-order.bin");
        storage.FactoryProfileRevision.ShouldBe(1, "purge must activate the profile revision stamped on the location, not a current route");
    }

    [Fact]
    public async Task Routed_purge_recovers_a_deleting_location_after_bytes_were_removed()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState
        {
            Capabilities = StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead
                | StorageProviderCapabilities.ConditionalCreate | StorageProviderCapabilities.Delete,
        };
        var bytes = "bytes removed before finalize"u8.ToArray();
        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-recover")))
            .ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        await MoveLocationAsync(committed.ArtifactLocationId, ArtifactLocationState.Deleting);
        storage.Objects.TryRemove("cas/purge-recover.bin", out _).ShouldBeTrue();

        using var purgeScope = Scope(storage);
        var result = await purgeScope.Resolve<IArtifactCasPurgeCoordinator>().PurgeAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId, ActorId = world.ActorId,
        }, CancellationToken.None);

        var purged = result.ShouldBeOfType<ArtifactCasPurgeResult.Purged>();
        purged.LocationRevision.ShouldBe(4, "the recovery advances Deleting before treating Missing as idempotent success, then finalizes Purged");
        purged.WasAlreadyPurged.ShouldBeFalse();
    }

    [Fact]
    public async Task Every_recovery_attempt_advances_the_deleting_claim_before_it_can_repeat_provider_io()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState
        {
            Capabilities = StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead
                | StorageProviderCapabilities.ConditionalCreate | StorageProviderCapabilities.Delete,
        };
        var bytes = "bytes awaiting recovery"u8.ToArray();
        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-reclaim")))
            .ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        await MoveLocationAsync(committed.ArtifactLocationId, ArtifactLocationState.Deleting);

        using var purgeScope = Scope(storage);
        var claimed = (await purgeScope.Resolve<IArtifactCasPurgeCoordinator>().ClaimAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId, ActorId = world.ActorId,
        }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeClaimResult.Claimed>();

        claimed.Claim.LocationRevision.ShouldBe(3, "recovery must claim Deleting again; reusing rev2 would make its byte effect invisible to a newer fence");
        storage.DeleteCalls.ShouldBe(0, "claiming is a short database effect and cannot perform provider I/O");
    }

    [Fact]
    public async Task A_claim_abandoned_before_provider_io_restores_the_available_location_with_an_event()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState
        {
            Capabilities = StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead
                | StorageProviderCapabilities.ConditionalCreate | StorageProviderCapabilities.Delete,
        };
        var bytes = "bytes a late reference keeps"u8.ToArray();
        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-release")))
            .ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        using var purgeScope = Scope(storage);
        var coordinator = purgeScope.Resolve<IArtifactCasPurgeCoordinator>();
        var claimed = (await coordinator.ClaimAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId, ActorId = world.ActorId,
        }, CancellationToken.None)).ShouldBeOfType<ArtifactCasPurgeClaimResult.Claimed>();
        (await coordinator.ReleaseAsync(claimed.Claim, CancellationToken.None)).ShouldBeTrue();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.AsNoTracking().SingleAsync(value => value.Id == committed.ArtifactLocationId);
        location.State.ShouldBe(ArtifactLocationState.Available);
        location.Revision.ShouldBe(3);
        (await db.ArtifactLocationEvent.CountAsync(value => value.ArtifactLocationId == location.Id)).ShouldBe(3);
        storage.Objects.ShouldContainKey("cas/purge-release.bin");
        storage.DeleteCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Routed_purge_fails_closed_before_claiming_an_object_with_multiple_locations()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState
        {
            Capabilities = StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead
                | StorageProviderCapabilities.ConditionalCreate | StorageProviderCapabilities.Delete,
        };
        var bytes = "replicated routed bytes"u8.ToArray();
        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-multi")))
            .ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        await AddSecondLocationAsync(world, committed.ArtifactObjectId, bytes);

        using var purgeScope = Scope(storage);
        var result = await purgeScope.Resolve<IArtifactCasPurgeCoordinator>().PurgeAsync(new ArtifactCasPurgeRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId, ActorId = world.ActorId,
        }, CancellationToken.None);

        result.ShouldBeOfType<ArtifactCasPurgeResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.MultipleLocationsUnsupported);
        storage.DeleteCalls.ShouldBe(0);
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(value => value.TeamId == world.TeamId && value.ArtifactObjectId == committed.ArtifactObjectId)
            .Select(value => value.State).ToListAsync()).ShouldAllBe(value => value == ArtifactLocationState.Available,
                "a fail-closed multi-location object must not have a partial deletion claim");
    }

    [Fact]
    public async Task Streaming_put_readback_commit_and_verified_stream_read_round_trip()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = Enumerable.Range(0, 600_000).Select(value => (byte)(value % 251)).ToArray();
        var source = new ObservedReadStream(bytes);

        var result = await PutAsync(world, storage, Request(world, source, bytes, "normal"));

        var committed = result.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        committed.WasAlreadyCommitted.ShouldBeFalse();
        source.MaxRequestedBytes.ShouldBeLessThanOrEqualTo(FakeStorageDriver.BufferSize);
        storage.PutCalls.ShouldBe(1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.Id == committed.IntentId);
            intent.State.ShouldBe(ArtifactTransferState.Committed);
            intent.WorkerFenceEpoch.ShouldBe(1);
            intent.ArtifactObjectId.ShouldBe(committed.ArtifactObjectId);
            intent.ArtifactLocationId.ShouldBe(committed.ArtifactLocationId);
            (await db.ArtifactObject.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
            (await db.ArtifactLocation.CountAsync(value => value.TeamId == world.TeamId && value.State == ArtifactLocationState.Available)).ShouldBe(1);
            (await db.ArtifactLocationEvent.CountAsync(value => value.TeamId == world.TeamId && value.EventType == ArtifactLocationEventType.Verified)).ShouldBe(1);
        }

        using var readScope = Scope(storage);
        var read = await readScope.Resolve<IArtifactCasRuntimeCoordinator>().OpenReadAsync(new ArtifactCasReadRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId,
            StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
        }, CancellationToken.None);
        var opened = read.ShouldBeOfType<ArtifactCasReadResult.Opened>();
        await using var verified = opened.Content;
        (await verified.ReadAsync(Memory<byte>.Empty)).ShouldBe(0);
        using var received = new MemoryStream();
        await verified.CopyToAsync(received);
        received.ToArray().ShouldBe(bytes);

        storage.CorruptReads = true;
        using var corruptScope = Scope(storage);
        var corruptRead = await corruptScope.Resolve<IArtifactCasRuntimeCoordinator>().OpenReadAsync(new ArtifactCasReadRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId,
            StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
        }, CancellationToken.None);
        await using var corruptStream = corruptRead.ShouldBeOfType<ArtifactCasReadResult.Opened>().Content;
        await Should.ThrowAsync<InvalidDataException>(() => corruptStream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task Duplicate_intent_and_content_return_existing_commit_without_second_provider_write()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(4096);

        var first = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "duplicate"));
        var second = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "duplicate"));

        var committed = first.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        var duplicate = second.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        duplicate.IntentId.ShouldBe(committed.IntentId);
        duplicate.ArtifactObjectId.ShouldBe(committed.ArtifactObjectId);
        duplicate.WasAlreadyCommitted.ShouldBeTrue();
        storage.PutCalls.ShouldBe(1);

        var foreignBytes = RandomNumberGenerator.GetBytes(4096);
        var conflict = await PutAsync(world, storage, Request(world, new MemoryStream(foreignBytes), foreignBytes, "duplicate"));
        conflict.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.IdempotencyConflict);
        storage.PutCalls.ShouldBe(1);
    }

    /// <summary>
    /// The dedup short-circuit is the whole cost case for content-addressed storage AND the one place a purge can hand
    /// a writer an object whose bytes are gone. <c>Available</c> is the only location state that means "these bytes
    /// were verified present here", so it is the only one that may satisfy a write without provider I/O.
    /// </summary>
    [Theory]
    [InlineData(ArtifactLocationState.Available, null)]
    [InlineData(ArtifactLocationState.Deleting, ArtifactCasProblemCode.TargetMissing)]
    [InlineData(ArtifactLocationState.Deleted, ArtifactCasProblemCode.TargetMissing)]
    [InlineData(ArtifactLocationState.Missing, ArtifactCasProblemCode.TargetMissing)]
    [InlineData(ArtifactLocationState.Corrupt, ArtifactCasProblemCode.TargetMissing)]
    public async Task Dedup_hit_satisfies_a_write_only_while_the_committed_location_is_still_available(ArtifactLocationState state, ArtifactCasProblemCode? expected)
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(4096);

        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "dedup-location"))).ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        if (state != ArtifactLocationState.Available) await MoveLocationAsync(committed.ArtifactLocationId, state);

        var second = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "dedup-location"));

        if (expected == null)
        {
            var hit = second.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
            hit.WasAlreadyCommitted.ShouldBeTrue();
            hit.IntentId.ShouldBe(committed.IntentId);
            hit.ArtifactObjectId.ShouldBe(committed.ArtifactObjectId);
            hit.ArtifactLocationId.ShouldBe(committed.ArtifactLocationId);
        }
        else
        {
            var rejected = second.ShouldBeOfType<ArtifactCasTransferResult.Rejected>();
            rejected.Problem.Code.ShouldBe(expected.Value);
            rejected.IntentId.ShouldBe(committed.IntentId);
        }

        // Either way the dedup decision costs no provider I/O: one driver and one upload for the whole test.
        storage.FactoryCreateCalls.ShouldBe(1);
        storage.PutCalls.ShouldBe(1);
    }

    /// <summary>
    /// The write-back half of a purge. A purged location is the only non-<c>Available</c> state a write may revive,
    /// and reviving it is what stops a purge from being data loss: the object key cannot take a second location row
    /// (<c>ux_artifact_location_profile_object_key</c>) and the commit only ever re-verifies the row that is there.
    ///
    /// <para>The re-put also has to get PAST the intent ledger to reach that commit. The first write's intent is
    /// <c>Committed</c> for good (0131 whitelists no transition out), so the repair is a fresh generation — the same
    /// mechanism a terminal transfer failure already uses.</para>
    /// </summary>
    [Fact]
    public async Task Purged_location_takes_the_same_content_again_under_a_fresh_generation()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var request = Request(world, new MemoryStream(bytes), bytes, "purge-rewrite");

        var first = (await PutAsync(world, storage, request)).ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        var purgedRevision = await PurgeLocationAsync(first.ArtifactLocationId, storage, request.TargetObjectKey);

        var again = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-rewrite"));

        var rewritten = again.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        rewritten.WasAlreadyCommitted.ShouldBeFalse("the bytes were gone, so this had to be a real transfer");
        rewritten.IntentId.ShouldNotBe(first.IntentId, "Committed is a one-way door; the repair is a new intent");
        rewritten.ArtifactObjectId.ShouldBe(first.ArtifactObjectId, "artifact_object rows are permanent tombstones — same content, same object");
        rewritten.ArtifactLocationId.ShouldBe(first.ArtifactLocationId, "the unique index forbids a second row for this key, so the purged row is what got revived");
        storage.PutCalls.ShouldBe(2);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var location = await db.ArtifactLocation.SingleAsync(value => value.Id == first.ArtifactLocationId);
            location.State.ShouldBe(ArtifactLocationState.Available);
            location.Revision.ShouldBe(purgedRevision + 1);
            location.LastErrorCode.ShouldBeNull();
            (await db.ArtifactLocation.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
            (await db.ArtifactObject.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
            var keys = await db.ArtifactTransferIntent.Where(value => value.TeamId == world.TeamId).OrderBy(value => value.IdempotencyKey).Select(value => value.IdempotencyKey).ToListAsync();
            keys.ShouldBe(new[] { "purge-rewrite", "purge-rewrite/g1" });
        }

        using var readScope = Scope(storage);
        var read = await readScope.Resolve<IArtifactCasRuntimeCoordinator>().OpenReadAsync(new ArtifactCasReadRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = rewritten.ArtifactObjectId,
            StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
        }, CancellationToken.None);
        await using var content = read.ShouldBeOfType<ArtifactCasReadResult.Opened>().Content;
        using var received = new MemoryStream();
        await content.CopyToAsync(received);
        received.ToArray().ShouldBe(bytes);
    }

    /// <summary>
    /// A generation, once spent, must stay spent. Reviving a purged location makes its generation usable again —
    /// <c>Available</c> content is exactly what a dedup hit wants — so a key derived by COUNTING the spent generations
    /// would drop back onto one it had already burned, and every later writer of this content would be handed that
    /// dead intent's stored verdict instead of a transfer. The newest generation is what the key follows.
    /// </summary>
    [Fact]
    public async Task A_revived_location_never_sends_the_next_writer_back_to_a_burned_generation()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var request = Request(world, new MemoryStream(bytes), bytes, "purge-generations");

        var first = (await PutAsync(world, storage, request)).ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        await PurgeLocationAsync(first.ArtifactLocationId, storage, request.TargetObjectKey);

        // A foreign object of the wrong length at the target key is a non-retryable TargetCorrupt, which burns g1.
        storage.Objects[request.TargetObjectKey] = RandomNumberGenerator.GetBytes(64);
        var burned = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-generations"));
        burned.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.TargetCorrupt);
        storage.Objects.TryRemove(request.TargetObjectKey, out _).ShouldBeTrue();

        var revived = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-generations"))).ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        var afterRevival = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "purge-generations"));

        var hit = afterRevival.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        hit.WasAlreadyCommitted.ShouldBeTrue("the revived location is Available again, so this is an ordinary dedup hit");
        hit.IntentId.ShouldBe(revived.IntentId, "a burned generation must never be handed back to a writer");

        using var scope = _fixture.BeginScope();
        var intents = await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent
            .Where(value => value.TeamId == world.TeamId).OrderBy(value => value.IdempotencyKey)
            .Select(value => new { value.IdempotencyKey, value.State }).ToListAsync();
        intents.Select(value => (value.IdempotencyKey, value.State)).ShouldBe(new[]
        {
            ("purge-generations", ArtifactTransferState.Committed),
            ("purge-generations/g1", ArtifactTransferState.Failed),
            ("purge-generations/g2", ArtifactTransferState.Committed),
        });
    }

    /// <summary>
    /// A purge moves the location between a writer's upload and that writer's commit. The commit must not write its
    /// observation onto the row, because the bytes it verified are exactly the ones that purge is entitled to remove.
    ///
    /// <para>Two shapes, from both pre-upload states. A purge still HOLDING the row is refused on the state — that is
    /// the <c>Deleting</c> claim #1532 already pinned. A purge that COMPLETED leaves the row back in the one state a
    /// write may revive, and then the only thing between this writer and a location claiming bytes nobody has is the
    /// revision it fenced on before uploading. This staging deliberately leaves the bytes in place for that case: the
    /// writer's readback succeeds and the row alone says a purge ran, which is all a writer can ever know.</para>
    /// </summary>
    [Theory]
    [InlineData(ArtifactLocationState.Available, ArtifactLocationState.Deleting)]
    [InlineData(ArtifactLocationState.Available, ArtifactLocationState.Purged)]
    [InlineData(ArtifactLocationState.Purged, ArtifactLocationState.Deleting)]
    [InlineData(ArtifactLocationState.Purged, ArtifactLocationState.Purged)]
    public async Task Purge_that_moves_the_location_between_upload_and_commit_admits_no_committed_artifact(ArtifactLocationState beforeUpload, ArtifactLocationState afterUpload)
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var seed = Request(world, new MemoryStream(bytes), bytes, "purge-race-seed") with { TargetObjectKey = "cas/purge-race.bin" };
        var seeded = (await PutAsync(world, storage, seed)).ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        if (beforeUpload == ArtifactLocationState.Purged) await PurgeLocationAsync(seeded.ArtifactLocationId, storage, seed.TargetObjectKey);
        else storage.Objects.TryRemove(seed.TargetObjectKey, out _).ShouldBeTrue();
        storage.BlockAfterNextPut = true;
        using var writerScope = Scope(storage);
        var writer = writerScope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(Request(world, new MemoryStream(bytes), bytes, "purge-race-writer") with { TargetObjectKey = seed.TargetObjectKey }, CancellationToken.None);
        await storage.BlockedAfterPutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var claimedRevision = await ObservePurgeAsync(seeded.ArtifactLocationId, afterUpload);
        storage.ReleaseBlockedAfterPut.TrySetResult();

        var result = await writer;

        result.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.IdempotencyConflict);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(value => value.Id == seeded.ArtifactLocationId);
        location.State.ShouldBe(afterUpload);
        location.Revision.ShouldBe(claimedRevision);
        (await db.ArtifactLocation.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
        (await db.ArtifactTransferIntent.CountAsync(value => value.TeamId == world.TeamId && value.State == ArtifactTransferState.Committed)).ShouldBe(1);
    }

    [Fact]
    public async Task Throttle_schedules_durable_retry_and_late_retry_reclaims_then_commits()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        storage.PutErrors.Enqueue(new ArtifactStorageError(ArtifactStorageErrorCode.Throttled, "provider-secret-must-not-persist", true));
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-15T00:00:00Z"));
        var bytes = RandomNumberGenerator.GetBytes(8192);

        var first = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "throttle"), clock);
        var deferred = first.ShouldBeOfType<ArtifactCasTransferResult.Deferred>();
        deferred.Problem.Code.ShouldBe(ArtifactCasProblemCode.Throttled);
        using (var errorScope = _fixture.BeginScope())
        {
            var stored = await errorScope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.SingleAsync(value => value.Id == deferred.IntentId);
            stored.LastErrorMessage.ShouldNotContain("provider-secret-must-not-persist");
            stored.LastErrorMessage.ShouldContain(nameof(ArtifactCasProblemCode.Throttled));
        }
        clock.Advance(TimeSpan.FromMinutes(1));

        var second = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "throttle"), clock);
        second.ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        using var scope = _fixture.BeginScope();
        var intent = await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.SingleAsync(value => value.Id == deferred.IntentId);
        intent.State.ShouldBe(ArtifactTransferState.Committed);
        intent.WorkerFenceEpoch.ShouldBe(2);
        intent.RetryCount.ShouldBe(1);
        intent.LastErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task Cancellation_preserves_uploading_intent_and_a_later_process_can_resume()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { BlockAfterNextPut = true };
        var bytes = RandomNumberGenerator.GetBytes(32_000);
        using var scope = Scope(storage);
        using var cancellation = new CancellationTokenSource();
        var request = Request(world, new MemoryStream(bytes), bytes, "cancel-resume") with { OperationTimeout = TimeSpan.FromMilliseconds(25) };
        var running = scope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(request, cancellation.Token);
        await storage.BlockedAfterPutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await running.ShouldThrowAsync<OperationCanceledException>();

        Guid intentId;
        using (var queryScope = _fixture.BeginScope())
        {
            var db = queryScope.Resolve<CodeSpaceDbContext>();
            var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == world.TeamId && value.IdempotencyKey == "cancel-resume");
            intent.State.ShouldBe(ArtifactTransferState.Uploading);
            intent.ArtifactObjectId.ShouldBeNull();
            intentId = intent.Id;
        }

        await Task.Delay(500);
        var resumed = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "cancel-resume"));
        resumed.ShouldBeOfType<ArtifactCasTransferResult.Committed>().IntentId.ShouldBe(intentId);
        storage.PutCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Live_lease_blocks_duplicate_then_expired_reclaim_fences_stale_provider_completion()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { BlockIgnoringCancellationNextPut = true };
        var bytes = RandomNumberGenerator.GetBytes(64_000);
        var request = Request(world, new MemoryStream(bytes), bytes, "concurrent") with { OperationTimeout = TimeSpan.FromMilliseconds(100) };
        using var firstScope = Scope(storage);
        var first = firstScope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(request, CancellationToken.None);
        await storage.IgnoringCancellationPutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var liveDuplicate = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "concurrent") with { OperationTimeout = TimeSpan.FromMilliseconds(100) });
        liveDuplicate.ShouldBeOfType<ArtifactCasTransferResult.Deferred>().Problem.Code.ShouldBe(ArtifactCasProblemCode.TransferInProgress);

        await Task.Delay(500);
        var second = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "concurrent") with { OperationTimeout = TimeSpan.FromMilliseconds(100) });
        second.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        storage.ReleaseIgnoringCancellationPut.TrySetResult();
        var stale = await first;
        stale.ShouldBeOfType<ArtifactCasTransferResult.Deferred>().Problem.Code.ShouldBe(ArtifactCasProblemCode.StaleWorker);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == world.TeamId && value.IdempotencyKey == "concurrent");
        intent.State.ShouldBe(ArtifactTransferState.Committed);
        intent.WorkerFenceEpoch.ShouldBe(2);
        (await db.ArtifactObject.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
        (await db.ArtifactLocation.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_distinct_intents_for_same_digest_and_target_share_one_object_and_location()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { BlockNextPut = true };
        var bytes = RandomNumberGenerator.GetBytes(48_000);
        var firstRequest = Request(world, new MemoryStream(bytes), bytes, "same-digest-a") with { TargetObjectKey = "cas/shared-digest.bin" };
        var secondRequest = Request(world, new MemoryStream(bytes), bytes, "same-digest-b") with { TargetObjectKey = "cas/shared-digest.bin" };
        using var firstScope = Scope(storage);
        var first = firstScope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(firstRequest, CancellationToken.None);
        await storage.BlockedPutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await PutAsync(world, storage, secondRequest);
        second.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        storage.ReleaseBlockedPut.TrySetResult();
        (await first).ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.ArtifactTransferIntent.CountAsync(value => value.TeamId == world.TeamId && value.State == ArtifactTransferState.Committed)).ShouldBe(2);
        (await db.ArtifactObject.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
        (await db.ArtifactLocation.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(1);
    }

    [Fact]
    public async Task Readback_digest_mismatch_fails_closed_without_admitting_object_or_location()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { CorruptReads = true };
        var bytes = RandomNumberGenerator.GetBytes(2048);

        var result = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "corrupt"));

        var rejected = result.ShouldBeOfType<ArtifactCasTransferResult.Rejected>();
        rejected.Problem.Code.ShouldBe(ArtifactCasProblemCode.TargetCorrupt);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.ArtifactTransferIntent.SingleAsync(value => value.Id == rejected.IntentId)).State.ShouldBe(ArtifactTransferState.Failed);
        (await db.ArtifactObject.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
        (await db.ArtifactLocation.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Provider_timeout_is_typed_and_preserves_retryable_intent()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { BlockNextPut = true };
        var bytes = RandomNumberGenerator.GetBytes(1024);
        var request = Request(world, new MemoryStream(bytes), bytes, "timeout") with { OperationTimeout = TimeSpan.FromMilliseconds(25) };

        var result = await PutAsync(world, storage, request);

        var deferred = result.ShouldBeOfType<ArtifactCasTransferResult.Deferred>();
        deferred.Problem.Code.ShouldBe(ArtifactCasProblemCode.ProviderTimeout);
        deferred.Problem.IsRetryable.ShouldBeTrue();
        storage.ReleaseBlockedPut.TrySetResult();
    }

    [Fact]
    public async Task Timed_out_write_does_not_return_or_dispose_while_provider_still_reads_caller_owned_stream()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { BlockIgnoringCancellationNextPut = true };
        var bytes = RandomNumberGenerator.GetBytes(2048);
        var request = Request(world, new MemoryStream(bytes), bytes, "ignores-cancellation") with { OperationTimeout = TimeSpan.FromMilliseconds(25) };

        var running = PutAsync(world, storage, request);
        await storage.IgnoringCancellationPutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(75);
        running.IsCompleted.ShouldBeFalse();
        storage.DisposeCalls.ShouldBe(0);
        storage.ReleaseIgnoringCancellationPut.TrySetResult();
        var result = await running;
        result.ShouldBeOfType<ArtifactCasTransferResult.Deferred>().Problem.Code.ShouldBe(ArtifactCasProblemCode.ProviderTimeout);
        await storage.DriverDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        storage.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Attempt_owned_request_fails_closed_until_authoritative_admission_is_available()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(256);
        var request = Request(world, new MemoryStream(bytes), bytes, "attempt-owned") with
        {
            ExecutionIdentity = new ArtifactCasExecutionIdentity(Guid.NewGuid(), 1, 1),
        };

        var result = await PutAsync(world, storage, request);

        result.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.ExecutionAdmissionUnavailable);
        storage.FactoryCreateCalls.ShouldBe(0);
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Reverified_reusable_location_appends_revision_and_refreshes_provider_conditions()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(2048);
        var first = Request(world, new MemoryStream(bytes), bytes, "metadata-a") with { TargetObjectKey = "cas/metadata.bin" };
        var second = Request(world, new MemoryStream(bytes), bytes, "metadata-b") with { TargetObjectKey = "cas/metadata.bin" };

        var committed = (await PutAsync(world, storage, first)).ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        storage.MetadataRevision = 2;
        (await PutAsync(world, storage, second)).ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var location = await db.ArtifactLocation.SingleAsync(value => value.Id == committed.ArtifactLocationId);
            location.Revision.ShouldBe(2);
            location.ProviderObjectVersion.ShouldBe("v2");
            (await db.ArtifactLocationEvent.CountAsync(value => value.ArtifactLocationId == location.Id)).ShouldBe(2);
        }

        using var readScope = Scope(storage);
        var read = await readScope.Resolve<IArtifactCasRuntimeCoordinator>().OpenReadAsync(new ArtifactCasReadRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId,
            StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
        }, CancellationToken.None);
        await using var content = read.ShouldBeOfType<ArtifactCasReadResult.Opened>().Content;
        await content.CopyToAsync(Stream.Null);
    }

    [Fact]
    public async Task Concurrent_valid_intents_retry_shared_location_xmin_collision_instead_of_reporting_stale()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var seed = Request(world, new MemoryStream(bytes), bytes, "location-race-seed") with { TargetObjectKey = "cas/location-race.bin" };
        var seeded = (await PutAsync(world, storage, seed)).ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        var barrier = new SharedLocationCommitBarrier();

        using var firstScope = Scope(storage, interceptor: barrier);
        using var secondScope = Scope(storage, interceptor: barrier);
        var first = firstScope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(Request(world, new MemoryStream(bytes), bytes, "location-race-a") with { TargetObjectKey = seed.TargetObjectKey }, CancellationToken.None);
        var second = secondScope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(Request(world, new MemoryStream(bytes), bytes, "location-race-b") with { TargetObjectKey = seed.TargetObjectKey }, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        results.ShouldAllBe(result => result is ArtifactCasTransferResult.Committed);
        using var queryScope = _fixture.BeginScope();
        var db = queryScope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(value => value.Id == seeded.ArtifactLocationId);
        location.Revision.ShouldBe(3);
        (await db.ArtifactLocationEvent.CountAsync(value => value.ArtifactLocationId == location.Id)).ShouldBe(3);
        (await db.ArtifactTransferIntent.CountAsync(value => value.TeamId == world.TeamId && value.State == ArtifactTransferState.Committed)).ShouldBe(3);
    }

    [Fact]
    public async Task Cross_tenant_profile_and_missing_physical_object_fail_with_typed_outcomes()
    {
        var world = await SeedWorldAsync();
        var other = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(512);
        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "read-missing"))).ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        var crossTenant = Request(world, new MemoryStream(bytes), bytes, "foreign") with { TeamId = other.TeamId };
        var rejected = await PutAsync(world, storage, crossTenant);
        rejected.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.ProfileMissing);

        storage.Objects.TryRemove("cas/read-missing.bin", out _);
        using var readScope = Scope(storage);
        var missing = await readScope.Resolve<IArtifactCasRuntimeCoordinator>().OpenReadAsync(new ArtifactCasReadRequest
        {
            TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId,
            StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
        }, CancellationToken.None);
        missing.ShouldBeOfType<ArtifactCasReadResult.Unavailable>().Problem.Code.ShouldBe(ArtifactCasProblemCode.TargetMissing);
    }

    [Fact]
    public async Task Committed_bytes_stay_readable_after_the_profile_is_disabled_and_then_retired()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(9_000);
        var committed = (await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "outlives-lifecycle"))).ShouldBeOfType<ArtifactCasTransferResult.Committed>();

        foreach (var state in new[] { StorageProfileState.Disabled, StorageProfileState.Retired })
        {
            await SetProfileStateAsync(world, state);

            using var readScope = Scope(storage);
            var read = await readScope.Resolve<IArtifactCasRuntimeCoordinator>().OpenReadAsync(new ArtifactCasReadRequest
            {
                TeamId = world.TeamId, ArtifactObjectId = committed.ArtifactObjectId,
                StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
            }, CancellationToken.None);

            var opened = read.ShouldBeOfType<ArtifactCasReadResult.Opened>($"a {state} profile must still serve the bytes its own revision stamped");
            await using var content = opened.Content;
            using var received = new MemoryStream();
            await content.CopyToAsync(received);
            received.ToArray().ShouldBe(bytes);
        }
    }

    [Theory]
    [InlineData(StorageProfileState.Disabled)]
    [InlineData(StorageProfileState.Retired)]
    public async Task New_writes_are_still_refused_once_the_profile_leaves_active(StorageProfileState state)
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(512);
        await SetProfileStateAsync(world, state);

        var result = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "write-after-lifecycle"));

        result.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.ProfileNotActive);
        storage.FactoryCreateCalls.ShouldBe(0);
        storage.PutCalls.ShouldBe(0);
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Encrypted_credential_backed_profile_activates_the_factory_without_exposing_secret_material()
    {
        var world = await SeedWorldAsync(withCredential: true);
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(128);

        var result = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "credential"));

        result.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        storage.FactoryCredentialHandleObserved.ShouldBeTrue();
        storage.FactoryCreateCalls.ShouldBe(1);
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.CountAsync(value => value.TeamId == world.TeamId && value.State == ArtifactTransferState.Committed)).ShouldBe(1);
    }

    [Fact]
    public async Task Revoked_credential_and_wrong_provider_fail_closed_before_factory_I_O()
    {
        var revoked = await SeedWorldAsync(withCredential: true);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var credential = await db.StorageCredential.SingleAsync(value => value.TeamId == revoked.TeamId);
            credential.State = StorageCredentialState.Revoked;
            credential.RevokedDate = DateTimeOffset.UtcNow;
            credential.RevokedBy = revoked.ActorId;
            await db.SaveChangesAsync();
        }
        var revokedStorage = new FakeStorageState();
        var revokedBytes = RandomNumberGenerator.GetBytes(128);

        var revokedResult = await PutAsync(revoked, revokedStorage, Request(revoked, new MemoryStream(revokedBytes), revokedBytes, "revoked-credential"));

        revokedResult.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.CredentialUnavailable);
        revokedStorage.FactoryCreateCalls.ShouldBe(0);

        var mismatched = await SeedWorldAsync(withCredential: true, credentialProviderMismatch: true);
        var mismatchedStorage = new FakeStorageState();
        var mismatchedBytes = RandomNumberGenerator.GetBytes(128);

        var mismatchedResult = await PutAsync(mismatched, mismatchedStorage, Request(mismatched, new MemoryStream(mismatchedBytes), mismatchedBytes, "provider-mismatch"));

        mismatchedResult.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.CredentialInvalid);
        mismatchedStorage.FactoryCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Broker_activation_uses_the_requested_revision_even_when_a_newer_revision_is_current()
    {
        var world = await SeedWorldAsync();
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.StorageProfileRevision.Add(new StorageProfileRevision
            {
                Id = Guid.NewGuid(), TeamId = world.TeamId, StorageProfileId = world.ProfileId, Revision = 2,
                ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{\"rootPath\":\"/unused/newer\"}",
                NamespaceFingerprint = $"sha256:{new string('c', 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
            });
            await db.SaveChangesAsync();
            await db.StorageProfile.Where(value => value.TeamId == world.TeamId && value.Id == world.ProfileId).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.CurrentRevision, 2));
        }
        var storage = new FakeStorageState();
        var bytes = RandomNumberGenerator.GetBytes(128);

        var result = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "exact-revision"));

        result.ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        storage.FactoryProfileRevision.ShouldBe(1);
        using var queryScope = _fixture.BeginScope();
        var committed = await queryScope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.SingleAsync(value => value.TeamId == world.TeamId && value.IdempotencyKey == "exact-revision");
        committed.StorageProfileRevisionId.ShouldBe(world.ProfileRevisionId);
    }

    [Fact]
    public async Task Capability_mismatch_fails_closed_and_cleans_up_the_broker_lease_without_provider_I_O()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { Capabilities = StorageProviderCapabilities.StreamingRead };
        var bytes = RandomNumberGenerator.GetBytes(128);

        var result = await PutAsync(world, storage, Request(world, new MemoryStream(bytes), bytes, "capability-mismatch"));

        result.ShouldBeOfType<ArtifactCasTransferResult.Rejected>().Problem.Code.ShouldBe(ArtifactCasProblemCode.Unsupported);
        storage.PutCalls.ShouldBe(0);
        storage.DisposeCalls.ShouldBe(1);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.ArtifactObject.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
        (await db.ArtifactLocation.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Cancellation_during_broker_activation_preserves_the_claim_for_a_later_retry()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { BlockFactoryCreate = true };
        var bytes = RandomNumberGenerator.GetBytes(128);
        using var scope = Scope(storage);
        using var cancellation = new CancellationTokenSource();
        var request = Request(world, new MemoryStream(bytes), bytes, "broker-cancel");
        var running = scope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(request, cancellation.Token);
        await storage.FactoryCreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await running.ShouldThrowAsync<OperationCanceledException>();

        using var queryScope = _fixture.BeginScope();
        var intent = await queryScope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.SingleAsync(value => value.TeamId == world.TeamId && value.IdempotencyKey == "broker-cancel");
        intent.State.ShouldBe(ArtifactTransferState.Intended);
        intent.WorkerFenceEpoch.ShouldBe(1);
        (await queryScope.Resolve<CodeSpaceDbContext>().ArtifactObject.CountAsync(value => value.TeamId == world.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Timed_out_broker_activation_disposes_a_late_driver_once_without_blocking_the_CAS_outcome()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState { BlockFactoryIgnoringCancellation = true };
        var bytes = RandomNumberGenerator.GetBytes(128);
        var request = Request(world, new MemoryStream(bytes), bytes, "broker-timeout") with { OperationTimeout = TimeSpan.FromMilliseconds(25) };

        var running = PutAsync(world, storage, request);
        await storage.FactoryCreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deferred = await running.WaitAsync(TimeSpan.FromSeconds(5));

        deferred.ShouldBeOfType<ArtifactCasTransferResult.Deferred>().Problem.Code.ShouldBe(ArtifactCasProblemCode.ProviderTimeout);
        storage.DisposeCalls.ShouldBe(0);
        storage.ReleaseFactoryCreate.TrySetResult();
        await storage.DriverDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        storage.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Caller_cancellation_wins_when_a_nonconforming_broker_translates_cancellation_into_an_exception()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var broker = new CancellationTranslatingBroker();
        var bytes = RandomNumberGenerator.GetBytes(128);
        using var scope = Scope(storage, broker: broker);
        using var cancellation = new CancellationTokenSource();
        var running = scope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(Request(world, new MemoryStream(bytes), bytes, "translated-caller-cancel"), cancellation.Token);
        await broker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await running.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Operation_timeout_wins_when_a_nonconforming_broker_translates_its_linked_cancellation_into_an_exception()
    {
        var world = await SeedWorldAsync();
        var storage = new FakeStorageState();
        var broker = new CancellationTranslatingBroker();
        var bytes = RandomNumberGenerator.GetBytes(128);
        var request = Request(world, new MemoryStream(bytes), bytes, "translated-timeout") with { OperationTimeout = TimeSpan.FromMilliseconds(25) };
        using var scope = Scope(storage, broker: broker);

        var result = await scope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        result.ShouldBeOfType<ArtifactCasTransferResult.Deferred>().Problem.Code.ShouldBe(ArtifactCasProblemCode.ProviderTimeout);
    }

    private async Task<ArtifactCasTransferResult> PutAsync(World world, FakeStorageState storage, ArtifactCasTransferRequest request, TimeProvider? clock = null)
    {
        using var scope = Scope(storage, clock);
        return await scope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(request, CancellationToken.None);
    }

    private ILifetimeScope Scope(FakeStorageState storage, TimeProvider? clock = null, SaveChangesInterceptor? interceptor = null, IStorageRuntimeDriverBroker? broker = null) => _fixture.BeginScope(builder =>
    {
        builder.RegisterInstance(new FakeFactoryCatalog(new FakeStorageFactory(storage))).As<IArtifactStorageDriverFactoryCatalog>().SingleInstance();
        if (clock != null) builder.RegisterInstance(clock).As<TimeProvider>().SingleInstance();
        if (broker != null) builder.RegisterInstance(broker).As<IStorageRuntimeDriverBroker>().SingleInstance();
        if (interceptor != null)
        {
            var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
            builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
        }
    });

    private async Task<World> SeedWorldAsync(bool withCredential = false, bool credentialProviderMismatch = false)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profileRevisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        string? credentialRef = null;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        db.User.Add(new User { Id = actorId, Email = $"cas-runtime-{actorId:N}@test.local", Name = $"cas-runtime-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"cas-runtime-{teamId:N}", Name = "CAS Runtime Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        if (withCredential)
        {
            var credential = new StorageCredential
            {
                Id = Guid.NewGuid(), TeamId = teamId, StableName = $"cas-{Guid.NewGuid():N}", CurrentRevision = 1,
                State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
            };
            credential.Revisions.Add(new StorageCredentialRevision
            {
                Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credential.Id, Revision = 1,
                ProviderTypeKey = credentialProviderMismatch ? "wrong-provider/v1" : LocalRwxArtifactStorageDriverFactory.TypeKey,
                EncryptedPayload = encryptor.Encrypt("{}"),
                SafeHint = "safe", EnvelopeFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = actorId,
            });
            db.StorageCredential.Add(credential);
            credentialRef = $"db:{credential.Id:D}:1";
        }

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"runtime-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = profileRevisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = "{\"rootPath\":\"/unused/fake\"}",
            CredentialRef = credentialRef, NamespaceFingerprint = $"sha256:{new string('a', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        return new World(teamId, actorId, profileId, profileRevisionId);
    }

    /// <summary>
    /// Stands in for the whole routed purge this lane does not build, in the order the reaper has to use it: claim the
    /// location with <c>Deleting</c> FIRST, then remove the bytes, then record <c>Purged</c>. Returns the purged
    /// revision, which is the fence a writer of this content is entitled to see unchanged.
    /// </summary>
    private async Task<long> PurgeLocationAsync(Guid locationId, FakeStorageState storage, string objectKey)
    {
        await MoveLocationAsync(locationId, ArtifactLocationState.Deleting);
        storage.Objects.TryRemove(objectKey, out _).ShouldBeTrue();
        return await MoveLocationAsync(locationId, ArtifactLocationState.Purged);
    }

    /// <summary>
    /// A purge's ROW observations without touching the bytes, ending in the given state — <c>Purged</c> goes through
    /// the claim the schema requires. Stages what a mid-flight writer can actually see, which is the row moving and
    /// never whether the bytes moved with it.
    /// </summary>
    private async Task<long> ObservePurgeAsync(Guid locationId, ArtifactLocationState state)
    {
        if (state == ArtifactLocationState.Purged) await MoveLocationAsync(locationId, ArtifactLocationState.Deleting);

        return await MoveLocationAsync(locationId, state);
    }

    /// <summary>
    /// Stands in for the routed purge this lane does not build: one observation moving the location off
    /// <c>Available</c>, appended exactly the way the schema requires. Returns the revision it wrote.
    /// </summary>
    private async Task<long> MoveLocationAsync(Guid locationId, ArtifactLocationState state)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(value => value.Id == locationId);
        location.State = state;
        location.Revision++;
        location.LastModifiedDate = DateTimeOffset.UtcNow;
        db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
            EventType = ArtifactLocationEventType.StateChanged, State = state, ObservedAt = location.LastModifiedDate,
            ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
            ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
            ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
            ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
            ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}", CreatedBy = location.CreatedBy,
        });
        await db.SaveChangesAsync();
        return location.Revision;
    }

    private async Task AddSecondLocationAsync(World world, Guid artifactObjectId, byte[] bytes)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = artifactObjectId,
            StorageProfileRevisionId = world.ProfileRevisionId, Locator = "cas/purge-multi-replica.bin",
            ObjectKey = "cas/purge-multi-replica.bin", ProviderETag = "replica-etag",
            ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = SHA256.HashData(bytes), ObservedSizeBytes = bytes.LongLength,
            State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = now,
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };
        location.Events.Add(new ArtifactLocationEvent
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactLocationId = location.Id, Revision = 1,
            EventType = ArtifactLocationEventType.Verified, State = ArtifactLocationState.Available, ObservedAt = now,
            ProviderETag = location.ProviderETag, ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm,
            ProviderChecksum = location.ProviderChecksum, ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = now,
            DetailsJson = "{}", CreatedBy = world.ActorId,
        });
        db.ArtifactLocation.Add(location);
        await db.SaveChangesAsync();
    }

    private async Task SetProfileStateAsync(World world, StorageProfileState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageProfile
            .Where(value => value.TeamId == world.TeamId && value.Id == world.ProfileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, state));
    }

    private static ArtifactCasTransferRequest Request(World world, Stream content, byte[] bytes, string key) => new()
    {
        TeamId = world.TeamId, StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
        IdempotencyScope = key, TargetObjectKey = $"cas/{key}.bin", Content = content,
        ExpectedSizeBytes = bytes.LongLength, ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ActorId = world.ActorId, ContentType = "application/octet-stream",
    };

    private sealed record World(Guid TeamId, Guid ActorId, Guid ProfileId, Guid ProfileRevisionId);

    private sealed class FakeFactoryCatalog(IArtifactStorageDriverFactory factory) : IArtifactStorageDriverFactoryCatalog
    {
        public IArtifactStorageDriverFactory? Get(string providerTypeKey) => string.Equals(providerTypeKey, factory.ProviderTypeKey, StringComparison.Ordinal) ? factory : null;
        public IArtifactStorageDriverFactory Require(string providerTypeKey) => Get(providerTypeKey) ?? throw new NotSupportedException();
    }

    private sealed class FakeStorageFactory(FakeStorageState state) : IArtifactStorageDriverFactory
    {
        public string ProviderTypeKey => LocalRwxArtifactStorageDriverFactory.TypeKey;

        public async ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref state.FactoryCreateCalls);
            state.FactoryCredentialHandleObserved = request.CredentialHandle?.UseSecret(secret => secret.ValueKind == JsonValueKind.Object) == true;
            state.FactoryProfileRevision = request.Profile.ProfileRevision;
            if (state.BlockFactoryCreate)
            {
                state.FactoryCreateEntered.TrySetResult();
                await state.ReleaseFactoryCreate.Task.WaitAsync(cancellationToken);
            }
            if (state.BlockFactoryIgnoringCancellation)
            {
                state.FactoryCreateEntered.TrySetResult();
                await state.ReleaseFactoryCreate.Task;
            }
            return new FakeStorageDriver(state);
        }
    }

    private sealed class CancellationTranslatingBroker : IStorageRuntimeDriverBroker
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { throw new IOException("provider detail must not escape"); }
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FakeStorageState
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<ArtifactStorageError> PutErrors { get; } = new();
        public TaskCompletionSource BlockedPutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlockedPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedAfterPutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlockedAfterPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource IgnoringCancellationPutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIgnoringCancellationPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DriverDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FactoryCreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFactoryCreate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockNextPut;
        public bool BlockAfterNextPut;
        public bool BlockIgnoringCancellationNextPut;
        public bool BlockFactoryCreate;
        public bool BlockFactoryIgnoringCancellation;
        public bool BlockNextDelete;
        public bool CorruptReads;
        public StorageProviderCapabilities Capabilities = StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.ConditionalCreate;
        public int MetadataRevision = 1;
        public int PutCalls;
        public int DeleteCalls;
        public int FactoryCreateCalls;
        public int DisposeCalls;
        public bool FactoryCredentialHandleObserved;
        public int FactoryProfileRevision;
    }

    private sealed class FakeStorageDriver(FakeStorageState state) : IArtifactStorageDriver
    {
        public const int BufferSize = 32 * 1024;

        public StorageProviderCapabilities Capabilities => state.Capabilities;

        public async ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref state.PutCalls);
            if (state.BlockIgnoringCancellationNextPut)
            {
                state.BlockIgnoringCancellationNextPut = false;
                state.IgnoringCancellationPutEntered.TrySetResult();
                await state.ReleaseIgnoringCancellationPut.Task;
            }
            if (state.BlockNextPut)
            {
                state.BlockNextPut = false;
                state.BlockedPutEntered.TrySetResult();
                await state.ReleaseBlockedPut.Task.WaitAsync(cancellationToken);
            }
            if (state.PutErrors.TryDequeue(out var error)) return ArtifactStoragePutResult.Failed(error);

            using var destination = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var read = await request.Content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var bytes = destination.ToArray();
            if (!state.Objects.TryAdd(request.ObjectKey, bytes))
                return ArtifactStoragePutResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.AlreadyExists, "exists"));
            if (state.BlockAfterNextPut)
            {
                state.BlockAfterNextPut = false;
                state.BlockedAfterPutEntered.TrySetResult();
                await state.ReleaseBlockedAfterPut.Task.WaitAsync(cancellationToken);
            }
            return ArtifactStoragePutResult.Stored(Metadata(request.ObjectKey, bytes));
        }

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(state.Objects.TryGetValue(request.ObjectKey, out var bytes)
                ? ArtifactStorageHeadResult.Found(Metadata(request.ObjectKey, bytes))
                : ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing")));

        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken)
        {
            if (!state.Objects.TryGetValue(request.ObjectKey, out var stored))
                return ValueTask.FromResult(ArtifactStorageReadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing")));
            var metadata = Metadata(request.ObjectKey, stored);
            if ((request.ExpectedETag != null && !string.Equals(request.ExpectedETag, metadata.ETag, StringComparison.Ordinal))
                || (request.ExpectedVersion != null && !string.Equals(request.ExpectedVersion, metadata.Version, StringComparison.Ordinal)))
                return ValueTask.FromResult(ArtifactStorageReadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.ConditionNotMet, "condition")));
            var bytes = stored.ToArray();
            if (state.CorruptReads && bytes.Length > 0) bytes[0] ^= 0xff;
            return ValueTask.FromResult(ArtifactStorageReadResult.Opened(new MemoryStream(bytes, writable: false), bytes.LongLength, bytes.LongLength, metadata));
        }

        public async ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref state.DeleteCalls);
            if (state.BlockNextDelete)
            {
                state.BlockNextDelete = false;
                state.DeleteEntered.TrySetResult();
                await state.ReleaseDelete.Task.WaitAsync(cancellationToken);
            }
            return state.Objects.TryRemove(request.ObjectKey, out _)
                ? ArtifactStorageDeleteResult.Removed()
                : ArtifactStorageDeleteResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing"));
        }

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Available, Latency = TimeSpan.Zero });

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref state.DisposeCalls);
            state.DriverDisposed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private ArtifactStorageObjectMetadata Metadata(string key, byte[] bytes)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new ArtifactStorageObjectMetadata { ObjectKey = key, Length = bytes.LongLength, Sha256 = sha, ETag = $"etag-{sha}-v{state.MetadataRevision}", Version = $"v{state.MetadataRevision}" };
        }
    }

    private sealed class ObservedReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int MaxRequestedBytes { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaxRequestedBytes = Math.Max(MaxRequestedBytes, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class SharedLocationCommitBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            if (context != null && context.ChangeTracker.Entries<ArtifactLocation>().Any(entry => entry.State == EntityState.Modified)
                && context.ChangeTracker.Entries<ArtifactTransferIntent>().Any(entry => entry.State == EntityState.Modified && entry.Entity.State == ArtifactTransferState.Committed))
            {
                if (Interlocked.Increment(ref _arrivals) >= 2) _release.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan value) => Interlocked.Add(ref _utcTicks, value.Ticks);
    }
}
