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

    private static ArtifactCasTransferRequest Request(World world, Stream content, byte[] bytes, string key) => new()
    {
        TeamId = world.TeamId, StorageProfileId = world.ProfileId, StorageProfileRevision = 1,
        IdempotencyKey = key, TargetObjectKey = $"cas/{key}.bin", Content = content,
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
        public bool BlockNextPut;
        public bool BlockAfterNextPut;
        public bool BlockIgnoringCancellationNextPut;
        public bool BlockFactoryCreate;
        public bool BlockFactoryIgnoringCancellation;
        public bool CorruptReads;
        public StorageProviderCapabilities Capabilities = StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.ConditionalCreate;
        public int MetadataRevision = 1;
        public int PutCalls;
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

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(state.Objects.TryRemove(request.ObjectKey, out _)
                ? ArtifactStorageDeleteResult.Removed()
                : ArtifactStorageDeleteResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Missing, "missing")));

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
