using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogRuntimeTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunLogRuntimeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Multi_segment_large_range_and_completion_are_streaming_contiguous_and_digest_verified()
    {
        var world = await SeedWorldAsync();
        var artifacts = new TestCas(_fixture);
        var service = Service(artifacts);
        var session = Guid.NewGuid();
        var opened = (await service.OpenAsync(Open(world, session, AgentRunLogKinds.Transcript), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var firstBytes = Enumerable.Range(0, 384 * 1024).Select(value => (byte)(value % 251)).ToArray();
        var secondBytes = Enumerable.Range(0, 384 * 1024).Select(value => (byte)((value + 17) % 251)).ToArray();

        var first = (await service.AppendAsync(Append(world, opened.Metadata.StreamId, session, 1, 0, firstBytes), CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        first.Metadata.TotalBytes.ShouldBe(firstBytes.Length);
        var second = (await service.AppendAsync(Append(world, opened.Metadata.StreamId, session, 2, firstBytes.Length, secondBytes), CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        second.Metadata.TotalBytes.ShouldBe(firstBytes.Length + secondBytes.Length);

        var range = (await service.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, opened.Metadata.StreamId, 0, firstBytes.Length + secondBytes.Length), CancellationToken.None)).ShouldBeOfType<AgentRunLogRangeResult.Available>();
        range.Bytes.ShouldBe(firstBytes.Concat(secondBytes).ToArray());

        var finalized = (await service.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = session, ExpectedRevision = second.Metadata.Revision,
            ExpectedSourceOffsetBytes = second.Metadata.SourceOffsetBytes,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>();
        var completed = (await service.CompleteAsync(new AgentRunLogCompleteRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = session, ExpectedRevision = finalized.Metadata.Revision,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        completed.Metadata.State.ShouldBe(AgentRunLogStreamState.Completed);
        completed.Metadata.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(firstBytes.Concat(secondBytes).ToArray())));
        (await service.AppendAsync(Append(world, opened.Metadata.StreamId, session, 3, completed.Metadata.TotalBytes, [1]), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.StreamTerminal);
    }

    [Fact]
    public async Task Same_fence_revise_spools_are_distinct_append_preserved_sessions_including_an_empty_final_spool()
    {
        var world = await SeedWorldAsync();
        var service = Service(new TestCas(_fixture));
        var firstSession = Guid.NewGuid();
        var first = (await service.OpenAsync(Open(world, firstSession, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var firstAppend = (await service.AppendAsync(Append(world, first.Metadata.StreamId, firstSession, 1, 0, "one"u8.ToArray()), CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        await service.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = first.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = firstSession, ExpectedRevision = firstAppend.Metadata.Revision,
            ExpectedSourceOffsetBytes = firstAppend.Metadata.SourceOffsetBytes,
        }, CancellationToken.None);

        var secondSession = Guid.NewGuid();
        var second = (await service.OpenAsync(Open(world, secondSession, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        second.CaptureSourceBaseOffsetBytes.ShouldBe(3);
        var secondAppend = (await service.AppendAsync(Append(world, first.Metadata.StreamId, secondSession, 2, 3, "two"u8.ToArray()), CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        await service.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = first.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = secondSession, ExpectedRevision = secondAppend.Metadata.Revision,
            ExpectedSourceOffsetBytes = secondAppend.Metadata.SourceOffsetBytes,
        }, CancellationToken.None);

        var emptySession = Guid.NewGuid();
        var empty = (await service.OpenAsync(Open(world, emptySession, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        empty.CaptureSourceBaseOffsetBytes.ShouldBe(6);
        await service.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = first.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = emptySession, ExpectedRevision = empty.Metadata.Revision,
            ExpectedSourceOffsetBytes = empty.Metadata.SourceOffsetBytes,
        }, CancellationToken.None);

        var bytes = (await service.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, first.Metadata.StreamId, 0, 6), CancellationToken.None)).ShouldBeOfType<AgentRunLogRangeResult.Available>();
        bytes.Bytes.ShouldBe("onetwo"u8.ToArray());
        using var scope = _fixture.BeginScope();
        var sessions = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureSession.AsNoTracking()
            .Where(value => value.StreamId == first.Metadata.StreamId).OrderBy(value => value.SourceBaseOffsetBytes).ThenBy(value => value.CreatedAt).ToListAsync();
        sessions.Select(value => value.CaptureSessionId).ShouldBe(new[] { firstSession, secondSession, emptySession });
        sessions.ShouldAllBe(value => value.State == AgentRunLogCaptureSessionState.Finalized);
        sessions.Select(value => (value.SourceBaseOffsetBytes, value.SourceOffsetBytes)).ShouldBe(new[] { (0L, 3L), (3L, 6L), (6L, 6L) });
    }

    [Fact]
    public async Task Pinned_log_route_stays_on_its_frozen_revision_and_never_uses_the_legacy_reserved_profile_name()
    {
        var world = await SeedWorldAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        AddProfile(db, world, Guid.NewGuid(), "agent-run-logs", now);
        AddProfile(db, world, Guid.NewGuid(), "another-active-profile", now);
        await db.SaveChangesAsync();
        await AddRouteAsync(db, world, world.StorageProfileId, StorageProfileRevisionMode.Pinned, 1);
        await AppendProfileRevisionAsync(world, world.StorageProfileId, 2, 'c');

        var result = await scope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None);

        result.ShouldBe(new AgentRunLogStorageResolution.Ready(world.StorageProfileId, 1));
    }

    [Fact]
    public async Task Current_at_write_uses_the_exact_current_route_revision_and_that_target_profiles_current_revision()
    {
        var world = await SeedWorldAsync();
        var secondProfileId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        AddProfile(db, world, secondProfileId, "second-log-profile", now);
        await db.SaveChangesAsync();
        var route = await AddRouteAsync(db, world, world.StorageProfileId, StorageProfileRevisionMode.CurrentAtWrite);

        var resolver = scope.Resolve<IAgentRunLogStorageResolver>();
        var first = await resolver.ResolveAsync(world.TeamId, CancellationToken.None);
        first.ShouldBe(new AgentRunLogStorageResolution.Ready(world.StorageProfileId, 1));

        await AppendProfileRevisionAsync(world, world.StorageProfileId, 2, 'c');
        await AppendProfileRevisionAsync(world, secondProfileId, 2, 'd');
        await AppendRouteRevisionAsync(world, route.Id, secondProfileId, StorageProfileRevisionMode.CurrentAtWrite);

        var second = await resolver.ResolveAsync(world.TeamId, CancellationToken.None);
        second.ShouldBe(new AgentRunLogStorageResolution.Ready(secondProfileId, 2));
    }

    [Fact]
    public async Task Missing_route_stays_typed_missing_when_the_deployment_did_not_qualify_local_rwx_as_shared()
    {
        var world = await SeedWorldAsync();
        var nonTemporaryExistingRoot = Path.Combine(Directory.GetCurrentDirectory(), "agent-log-unqualified", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nonTemporaryExistingRoot);
        using var settings = RuntimeSettings.Override(current => current with
        {
            ArtifactStoreDirectory = nonTemporaryExistingRoot,
            ArtifactLocalRwxShared = false,
        });
        try
        {
            using var scope = _fixture.BeginScope();
            var result = await scope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None);

            result.ShouldBe(new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Missing));
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.StorageProfile.CountAsync(value => value.TeamId == world.TeamId && value.StableName == AgentRunLogStorageReadiness.DefaultProfileStableName)).ShouldBe(0);
            (await db.StorageRoute.CountAsync(value => value.TeamId == world.TeamId && value.DataClassTypeKey == AgentRunLogStorageResolver.DataClassTypeKey)).ShouldBe(0);
        }
        finally
        {
            Directory.Delete(nonTemporaryExistingRoot, recursive: true);
        }
    }

    [Fact]
    public async Task First_resolution_creates_one_idempotent_settings_visible_local_route_without_selecting_an_unrelated_profile()
    {
        var world = await SeedWorldAsync();
        var rootPath = Path.Combine(Path.GetTempPath(), "codespace-agent-log-readiness", Guid.NewGuid().ToString("N"));
        using var settings = RuntimeSettings.Override(current => current with { ArtifactStoreDirectory = rootPath, ArtifactLocalRwxShared = true });
        using var scope = _fixture.BeginScope();
        var resolver = scope.Resolve<IAgentRunLogStorageResolver>();

        var first = (await resolver.ResolveAsync(world.TeamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogStorageResolution.Ready>();
        var second = (await resolver.ResolveAsync(world.TeamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogStorageResolution.Ready>();

        first.ShouldBe(second);
        first.StorageProfileId.ShouldNotBe(world.StorageProfileId, "an arbitrary active profile is never a routing fallback");
        var db = scope.Resolve<CodeSpaceDbContext>();
        var route = await db.StorageRoute.AsNoTracking().Include(value => value.Revisions)
            .SingleAsync(value => value.TeamId == world.TeamId && value.DataClassTypeKey == AgentRunLogStorageResolver.DataClassTypeKey);
        route.State.ShouldBe(StorageRouteState.Active);
        route.CurrentRevision.ShouldBe(1);
        route.Revisions.ShouldHaveSingleItem().StorageProfileId.ShouldBe(first.StorageProfileId);
        route.Revisions.Single().ProfileRevisionMode.ShouldBe(StorageProfileRevisionMode.CurrentAtWrite);
        var profile = await db.StorageProfile.AsNoTracking().Include(value => value.Revisions).SingleAsync(value => value.TeamId == world.TeamId && value.Id == first.StorageProfileId);
        profile.StableName.ShouldBe(AgentRunLogStorageReadiness.DefaultProfileStableName);
        profile.State.ShouldBe(StorageProfileState.Active);
        profile.CreatedBy.ShouldBe(SystemUsers.SeederId);
        var revision = profile.Revisions.ShouldHaveSingleItem();
        revision.ProviderTypeKey.ShouldBe("local-rwx/v1");
        revision.CredentialRef.ShouldBeNull();
        using var config = JsonDocument.Parse(revision.NonSecretConfigJson);
        config.RootElement.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(rootPath));
    }

    [Fact]
    public async Task Reserved_profile_collision_is_not_hijacked_and_missing_route_stays_explicit()
    {
        var world = await SeedWorldAsync();
        var rootPath = Path.Combine(Path.GetTempPath(), "codespace-agent-log-collision", Guid.NewGuid().ToString("N"));
        using var settings = RuntimeSettings.Override(current => current with { ArtifactStoreDirectory = rootPath, ArtifactLocalRwxShared = true });
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        AddReservedDefaultProfile(db, world, rootPath, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var result = await scope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None);

        result.ShouldBe(new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Missing));
        (await db.StorageRoute.CountAsync(value => value.TeamId == world.TeamId && value.DataClassTypeKey == AgentRunLogStorageResolver.DataClassTypeKey)).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_first_resolutions_converge_on_one_profile_route_and_revision_chain()
    {
        var world = await SeedWorldAsync();
        using var settings = RuntimeSettings.Override(current => current with { ArtifactLocalRwxShared = true });
        using var firstScope = _fixture.BeginScope();
        using var secondScope = _fixture.BeginScope();

        var resolutions = await Task.WhenAll(
            firstScope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None),
            secondScope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None));

        resolutions.ShouldAllBe(value => value is AgentRunLogStorageResolution.Ready);
        resolutions.Cast<AgentRunLogStorageResolution.Ready>().Select(value => value.StorageProfileId).Distinct().Count().ShouldBe(1);
        using var readScope = _fixture.BeginScope();
        var db = readScope.Resolve<CodeSpaceDbContext>();
        (await db.StorageProfile.CountAsync(value => value.TeamId == world.TeamId && value.StableName == AgentRunLogStorageReadiness.DefaultProfileStableName)).ShouldBe(1);
        (await db.StorageRoute.CountAsync(value => value.TeamId == world.TeamId && value.DataClassTypeKey == AgentRunLogStorageResolver.DataClassTypeKey)).ShouldBe(1);
        (await db.StorageRouteRevision.CountAsync(value => value.TeamId == world.TeamId && value.Route.DataClassTypeKey == AgentRunLogStorageResolver.DataClassTypeKey)).ShouldBe(1);
    }

    [Fact]
    public async Task Default_route_persists_and_reads_log_bytes_through_the_profile_driven_local_runtime()
    {
        var world = await SeedWorldAsync();
        var rootPath = Path.Combine(Path.GetTempPath(), "codespace-agent-log-default-runtime", Guid.NewGuid().ToString("N"));
        using var settings = RuntimeSettings.Override(current => current with { ArtifactStoreDirectory = rootPath, ArtifactLocalRwxShared = true });
        try
        {
            var captureSessionId = Guid.NewGuid();
            var bytes = "durable-default-log"u8.ToArray();
            Guid streamId;

            using (var writeScope = _fixture.BeginScope())
            {
                var storage = (await writeScope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogStorageResolution.Ready>();
                var logs = writeScope.Resolve<IAgentRunLogService>();
                var opened = (await logs.OpenAsync(Open(world, captureSessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
                streamId = opened.Metadata.StreamId;
                var appended = await logs.AppendAsync(Append(world, streamId, captureSessionId, 1, 0, bytes) with
                {
                    StorageProfileId = storage.StorageProfileId,
                    StorageProfileRevision = storage.StorageProfileRevision,
                }, CancellationToken.None);

                appended.ShouldBeOfType<AgentRunLogAppendResult.Appended>();
            }

            using (var readScope = _fixture.BeginScope())
            {
                var read = (await readScope.Resolve<IAgentRunLogService>().ReadRangeAsync(
                    new AgentRunLogRangeRequest(world.TeamId, streamId, 0, bytes.Length), CancellationToken.None)).ShouldBeOfType<AgentRunLogRangeResult.Available>();
                read.Bytes.ShouldBe(bytes);
            }
            Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ShouldNotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Log_bytes_stay_available_to_the_read_api_after_their_storage_profile_is_disabled_and_retired()
    {
        var world = await SeedWorldAsync();
        var rootPath = Path.Combine(Path.GetTempPath(), "codespace-agent-log-retired-profile", Guid.NewGuid().ToString("N"));
        using var settings = RuntimeSettings.Override(current => current with { ArtifactStoreDirectory = rootPath, ArtifactLocalRwxShared = true });
        try
        {
            var captureSessionId = Guid.NewGuid();
            var bytes = "history-outlives-its-profile"u8.ToArray();
            Guid streamId;
            Guid storageProfileId;
            long revision;

            using (var writeScope = _fixture.BeginScope())
            {
                var storage = (await writeScope.Resolve<IAgentRunLogStorageResolver>().ResolveAsync(world.TeamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogStorageResolution.Ready>();
                storageProfileId = storage.StorageProfileId;
                var logs = writeScope.Resolve<IAgentRunLogService>();
                var opened = (await logs.OpenAsync(Open(world, captureSessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
                streamId = opened.Metadata.StreamId;
                var appended = (await logs.AppendAsync(Append(world, streamId, captureSessionId, 1, 0, bytes) with
                {
                    StorageProfileId = storage.StorageProfileId,
                    StorageProfileRevision = storage.StorageProfileRevision,
                }, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
                var finalized = (await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
                {
                    TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = streamId,
                    WorkerFenceEpoch = 7, CaptureSessionId = captureSessionId, ExpectedRevision = appended.Metadata.Revision,
                    ExpectedSourceOffsetBytes = appended.Metadata.SourceOffsetBytes,
                }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>();
                revision = finalized.Metadata.Revision;
            }

            // Written straight to the ledger: SetStateAsync now refuses this exact transition, but deployments that
            // already disabled or retired a profile must still be able to read the history stamped under it.
            await SetProfileStateAsync(storageProfileId, StorageProfileState.Disabled);
            (await ReadAvailabilityAsync(world, streamId, bytes)).ShouldBe(AgentRunLogReadAvailability.Available);

            using (var completeScope = _fixture.BeginScope())
            {
                var completed = (await completeScope.Resolve<IAgentRunLogService>().CompleteAsync(new AgentRunLogCompleteRequest
                {
                    TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = streamId,
                    WorkerFenceEpoch = 7, CaptureSessionId = captureSessionId, ExpectedRevision = revision,
                }, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
                completed.Metadata.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }

            await SetProfileStateAsync(storageProfileId, StorageProfileState.Retired);
            (await ReadAvailabilityAsync(world, streamId, bytes)).ShouldBe(AgentRunLogReadAvailability.Available);
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        }
    }

    private async Task<AgentRunLogReadAvailability> ReadAvailabilityAsync(World world, Guid streamId, byte[] expected)
    {
        using var scope = _fixture.BeginScopeAs(world.ActorId, world.TeamId);
        var handler = new ReadAgentRunLogRangeQueryHandler(scope.Resolve<IAgentRunLogService>(), scope.Resolve<ICurrentTeam>());
        var read = await handler.Handle(new ReadAgentRunLogRangeQuery { AgentRunId = world.AgentRunId, StreamId = streamId, LimitBytes = expected.Length }, CancellationToken.None);
        read.ShouldNotBeNull();
        if (read.Availability == AgentRunLogReadAvailability.Available) read.Content.ShouldBe(expected);
        return read.Availability;
    }

    private async Task SetProfileStateAsync(Guid profileId, StorageProfileState state)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().StorageProfile.Where(value => value.Id == profileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.State, state));
    }

    [Fact]
    public async Task Missing_routes_get_tenant_defaults_while_inactive_invalid_routes_remain_authoritative_without_fallback()
    {
        var world = await SeedWorldAsync();
        var foreign = await SeedWorldAsync();
        using var settings = RuntimeSettings.Override(current => current with { ArtifactLocalRwxShared = true });
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var resolver = scope.Resolve<IAgentRunLogStorageResolver>();

        var ready = (await resolver.ResolveAsync(world.TeamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogStorageResolution.Ready>();
        var foreignReady = (await resolver.ResolveAsync(foreign.TeamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogStorageResolution.Ready>();
        ready.StorageProfileId.ShouldNotBe(foreignReady.StorageProfileId);
        var route = await db.StorageRoute.SingleAsync(value => value.TeamId == world.TeamId && value.DataClassTypeKey == AgentRunLogStorageResolver.DataClassTypeKey);

        route.State = StorageRouteState.Disabled;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        (await resolver.ResolveAsync(world.TeamId, CancellationToken.None)).ShouldBe(new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Inactive));

        route.State = StorageRouteState.Active;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE storage_route_revision DISABLE TRIGGER storage_route_revision_enforce_append_only");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE storage_route_revision DROP CONSTRAINT ck_storage_route_revision_profile_selection");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE storage_route_revision SET profile_revision_mode = 'FuturePolicy' WHERE storage_route_id = {route.Id}");
        (await resolver.ResolveAsync(world.TeamId, CancellationToken.None)).ShouldBe(new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Invalid));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Exact_append_retry_is_idempotent_while_gap_overlap_and_changed_bytes_fail_closed()
    {
        var world = await SeedWorldAsync();
        var service = Service(new TestCas(_fixture));
        var session = Guid.NewGuid();
        var stream = (await service.OpenAsync(Open(world, session, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata;
        var request = Append(world, stream.StreamId, session, 1, 0, [1, 2, 3, 4]) with { SourceLengthBytes = 7 };
        var first = (await service.AppendAsync(request, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        first.Metadata.SourceOffsetBytes.ShouldBe(7, "source progress is independent from the redacted stored length");
        var retry = (await service.AppendAsync(request, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        retry.WasExactRetry.ShouldBeTrue();
        retry.Segment.SegmentId.ShouldBe(first.Segment.SegmentId);
        (await service.AppendAsync(request with { Bytes = new byte[] { 9, 9, 9, 9 } }, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.IdempotencyConflict);
        (await service.AppendAsync(request with { SourceLengthBytes = 6 }, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.IdempotencyConflict);
        (await service.AppendAsync(request with { ExpectedSegmentOrdinal = 2, ExpectedOffsetBytes = 3 }, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.NonContiguous);
        (await service.AppendAsync(request with { ExpectedSegmentOrdinal = 3, ExpectedOffsetBytes = 4 }, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.NonContiguous);
    }

    [Fact]
    public async Task Capture_failure_is_a_fenced_typed_terminal_stream_health_state_and_streams_are_listable_by_run()
    {
        var world = await SeedWorldAsync();
        var service = Service(new TestCas(_fixture));
        var session = Guid.NewGuid();
        var stdout = (await service.OpenAsync(Open(world, session, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata;
        var stderr = (await service.OpenAsync(Open(world, session, AgentRunLogKinds.StandardError), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata;

        var failed = await service.FailCaptureAsync(new AgentRunLogFailCaptureRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = stderr.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = session, ExpectedRevision = stderr.Revision,
            ErrorCode = "capture-backend-unavailable", ErrorMessage = "The selected capture backend was unavailable.",
        }, CancellationToken.None);

        failed.ShouldBeOfType<AgentRunLogFailCaptureResult.Failed>().Metadata.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
        var listed = await service.ListMetadataAsync(world.TeamId, world.AgentRunId, CancellationToken.None);
        listed.Select(value => value.StreamId).ShouldBe(new[] { stdout.StreamId, stderr.StreamId }, ignoreOrder: true);
        listed.Single(value => value.StreamId == stderr.StreamId).ErrorCode.ShouldBe("capture-backend-unavailable");
    }

    [Fact]
    public async Task Concurrent_exact_append_has_one_segment_and_both_callers_observe_the_same_receipt()
    {
        var world = await SeedWorldAsync();
        var artifacts = new TestCas(_fixture);
        var session = Guid.NewGuid();
        var opened = (await Service(artifacts).OpenAsync(Open(world, session, AgentRunLogKinds.Debug), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var request = Append(world, opened.Metadata.StreamId, session, 1, 0, Enumerable.Repeat((byte)42, 64 * 1024).ToArray());
        var results = await Task.WhenAll(Service(artifacts).AppendAsync(request, CancellationToken.None), Service(artifacts).AppendAsync(request, CancellationToken.None));

        results.ShouldAllBe(result => result is AgentRunLogAppendResult.Appended);
        results.Cast<AgentRunLogAppendResult.Appended>().Select(result => result.Segment.SegmentId).Distinct().Count().ShouldBe(1);
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().AgentRunLogSegment.CountAsync(value => value.StreamId == opened.Metadata.StreamId)).ShouldBe(1);
    }

    [Fact]
    public async Task Team_scope_stale_fence_and_missing_required_bytes_are_typed()
    {
        var world = await SeedWorldAsync();
        var artifacts = new TestCas(_fixture);
        var service = Service(artifacts);
        var session = Guid.NewGuid();
        var stream = (await service.OpenAsync(Open(world, session, AgentRunLogKinds.StandardError), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata;
        var appended = (await service.AppendAsync(Append(world, stream.StreamId, session, 1, 0, [5, 6, 7]), CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();

        (await service.GetMetadataAsync(Guid.NewGuid(), stream.StreamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogMetadataResult.Missing>();
        (await service.AppendAsync(Append(world with { TeamId = Guid.NewGuid() }, stream.StreamId, session, 2, 3, [8]), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.Missing);
        artifacts.FailReads(new ArtifactCasProblem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
        var unavailable = (await service.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, stream.StreamId, 0, 3), CancellationToken.None)).ShouldBeOfType<AgentRunLogRangeResult.Unavailable>();
        unavailable.Problem.Code.ShouldBe(AgentRunLogProblemCode.BackendUnavailable);
        unavailable.Problem.IsRetryable.ShouldBeTrue();
        artifacts.FailReads(null);
        artifacts.Replace(appended.Segment.ArtifactObjectId, [9, 9, 9]);
        (await service.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, stream.StreamId, 0, 3), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogRangeResult.Unavailable>().Problem.Code.ShouldBe(AgentRunLogProblemCode.ArtifactCorrupt);
        artifacts.Remove(appended.Segment.ArtifactObjectId);
        (await service.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, stream.StreamId, 0, 3), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogRangeResult.Unavailable>().Problem.Code.ShouldBe(AgentRunLogProblemCode.ArtifactMissing);

        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<CodeSpaceDbContext>().AgentRun.Where(value => value.TeamId == world.TeamId && value.Id == world.AgentRunId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.FenceEpoch, 8));
        }
        (await service.AppendAsync(Append(world, stream.StreamId, session, 2, 3, [8]), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.StaleWorker);
    }

    [Fact]
    public async Task Metadata_index_is_team_scoped_and_keyset_paged_without_loading_segments()
    {
        var world = await SeedWorldAsync();
        var service = Service(new TestCas(_fixture));
        var session = Guid.NewGuid();
        await service.OpenAsync(Open(world, session, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await Task.Delay(2);
        await service.OpenAsync(Open(world, session, AgentRunLogKinds.StandardError), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var first = await new ListAgentRunLogsQueryHandler(scope.Resolve<CodeSpaceDbContext>(), new StubCurrentTeam(world.TeamId))
            .Handle(new ListAgentRunLogsQuery { AgentRunId = world.AgentRunId, Limit = 1 }, CancellationToken.None);
        first.ShouldNotBeNull();
        first.Items.Count.ShouldBe(1);
        first.NextCursor.ShouldNotBeNull();

        var second = await new ListAgentRunLogsQueryHandler(scope.Resolve<CodeSpaceDbContext>(), new StubCurrentTeam(world.TeamId))
            .Handle(new ListAgentRunLogsQuery { AgentRunId = world.AgentRunId, Limit = 1, Cursor = first.NextCursor }, CancellationToken.None);
        second.ShouldNotBeNull();
        second.Items.Count.ShouldBe(1);
        second.Items[0].StreamId.ShouldNotBe(first.Items[0].StreamId);
        second.NextCursor.ShouldBeNull();

        var foreign = await new ListAgentRunLogsQueryHandler(scope.Resolve<CodeSpaceDbContext>(), new StubCurrentTeam(Guid.NewGuid()))
            .Handle(new ListAgentRunLogsQuery { AgentRunId = world.AgentRunId }, CancellationToken.None);
        foreign.ShouldBeNull();
    }

    private AgentRunLogService Service(IArtifactCasRuntimeCoordinator artifacts)
    {
        using var scope = _fixture.BeginScope();
        return new AgentRunLogService(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), artifacts, TimeProvider.System);
    }

    private static AgentRunLogOpenRequest Open(World world, Guid session, string kind) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, WorkerFenceEpoch = 7, CaptureSessionId = session,
        StreamKind = kind, ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "test-capture/v1",
    };

    private static AgentRunLogAppendRequest Append(World world, Guid streamId, Guid session, long ordinal, long offset, byte[] bytes) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = streamId, WorkerFenceEpoch = 7,
        CaptureSessionId = session, ExpectedSegmentOrdinal = ordinal, ExpectedOffsetBytes = offset,
        ExpectedSourceOffsetBytes = offset, SourceLengthBytes = bytes.Length,
        StorageProfileId = world.StorageProfileId, StorageProfileRevision = 1, ActorId = world.ActorId, Bytes = bytes,
    };

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"agent-log-runtime-{actorId:N}@test.local", Name = "Agent Log Runtime" });
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-log-runtime-{teamId:N}", Name = "Agent Log Runtime", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"agent-log-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1, ProviderTypeKey = "local-rwx/v1",
            NonSecretConfigJson = "{\"rootPath\":\"/srv/codespace/artifacts\"}", NamespaceFingerprint = $"sha256:{new string('a', 64)}",
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        var runId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = 7, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return new World(teamId, actorId, profileId, runId);
    }

    private static async Task<StorageRoute> AddRouteAsync(CodeSpaceDbContext db, World world, Guid profileId, StorageProfileRevisionMode mode, int? pinnedRevision = null)
    {
        var now = DateTimeOffset.UtcNow;
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, DataClassTypeKey = AgentRunLogStorageResolver.DataClassTypeKey,
            CurrentRevision = 1, State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = world.ActorId,
            LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };
        route.Revisions.Add(RouteRevision(world, route, profileId, mode, pinnedRevision));
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();
        route.State = StorageRouteState.Active;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return route;
    }

    private async Task AppendProfileRevisionAsync(World world, Guid profileId, int revision, char fingerprint)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var profile = await db.StorageProfile.SingleAsync(value => value.TeamId == world.TeamId && value.Id == profileId);
        profile.CurrentRevision = revision;
        profile.LastModifiedDate = DateTimeOffset.UtcNow;
        profile.LastModifiedBy = world.ActorId;
        db.StorageProfileRevision.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, StorageProfileId = profileId, Revision = revision,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = $"{{\"rootPath\":\"/srv/codespace/logs-v{revision}\"}}",
            NamespaceFingerprint = $"sha256:{new string(fingerprint, 64)}", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
        });
        await db.SaveChangesAsync();
    }

    private async Task AppendRouteRevisionAsync(World world, Guid routeId, Guid profileId, StorageProfileRevisionMode mode, int? pinnedRevision = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var route = await db.StorageRoute.SingleAsync(value => value.TeamId == world.TeamId && value.Id == routeId);
        route.CurrentRevision++;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        route.LastModifiedBy = world.ActorId;
        db.StorageRouteRevision.Add(RouteRevision(world, route, profileId, mode, pinnedRevision));
        await db.SaveChangesAsync();
    }

    private static StorageRouteRevision RouteRevision(World world, StorageRoute route, Guid profileId, StorageProfileRevisionMode mode, int? pinnedRevision = null) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision,
        StorageProfileId = profileId, ProfileRevisionMode = mode,
        PinnedProfileRevision = mode == StorageProfileRevisionMode.Pinned ? pinnedRevision ?? 1 : null,
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
    };

    private static void AddProfile(CodeSpaceDbContext db, World world, Guid profileId, string stableName, DateTimeOffset now)
    {
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = world.TeamId, StableName = stableName, State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = "{\"rootPath\":\"/srv/codespace/artifacts\"}",
            NamespaceFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = world.ActorId,
        });
        db.StorageProfile.Add(profile);
    }

    private static void AddReservedDefaultProfile(CodeSpaceDbContext db, World world, string rootPath, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath = Path.GetFullPath(rootPath) }));
        var canonicalConfig = StorageProfileRules.CanonicalJson(document.RootElement);
        using var canonical = JsonDocument.Parse(canonicalConfig);
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, StableName = AgentRunLogStorageReadiness.DefaultProfileStableName,
            State = StorageProfileState.Active, CurrentRevision = 1, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = canonicalConfig, CredentialRef = null,
            NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint("local-rwx/v1", canonical.RootElement),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        db.StorageProfile.Add(profile);
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileId, Guid AgentRunId);

    private sealed class StubCurrentTeam(Guid id) : ICurrentTeam
    {
        public Guid? Id { get; } = id;
        public bool IsSet => true;
    }

    private sealed class TestCas(PostgresFixture fixture) : IArtifactCasRuntimeCoordinator
    {
        private readonly ConcurrentDictionary<Guid, byte[]> _bytes = new();
        private readonly ConcurrentDictionary<string, Stored> _idempotency = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile ArtifactCasProblem? _readProblem;

        public async Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken)
        {
            using var content = new MemoryStream();
            await request.Content.CopyToAsync(content, cancellationToken);
            var bytes = content.ToArray();
            if (bytes.LongLength != request.ExpectedSizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != request.ExpectedSha256)
                return new ArtifactCasTransferResult.Rejected(null, new ArtifactCasProblem(ArtifactCasProblemCode.TargetCorrupt, false));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_idempotency.TryGetValue(request.IdempotencyScope, out var prior))
                    return prior.Sha256 == request.ExpectedSha256
                        ? new ArtifactCasTransferResult.Committed(prior.IntentId, prior.ObjectId, prior.LocationId, true)
                        : new ArtifactCasTransferResult.Rejected(prior.IntentId, new ArtifactCasProblem(ArtifactCasProblemCode.IdempotencyConflict, false));
                using var scope = fixture.BeginScope();
                var db = scope.Resolve<CodeSpaceDbContext>();
                var revision = await db.StorageProfileRevision.SingleAsync(value => value.TeamId == request.TeamId && value.StorageProfileId == request.StorageProfileId && value.Revision == request.StorageProfileRevision, cancellationToken);
                var objectId = Guid.NewGuid();
                var locationId = Guid.NewGuid();
                var intentId = Guid.NewGuid();
                var digest = SHA256.HashData(bytes);
                var now = DateTimeOffset.UtcNow;
                db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = request.TeamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256, Digest = digest, SizeBytes = bytes.Length, CreatedDate = now, CreatedBy = request.ActorId });
                var location = new ArtifactLocation
                {
                    Id = locationId, TeamId = request.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revision.Id,
                    Locator = $"test://{objectId:N}", ObjectKey = request.TargetObjectKey, ProviderObjectVersion = "v1", ProviderETag = request.ExpectedSha256,
                    ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = digest, ObservedSizeBytes = bytes.Length,
                    State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = now,
                    CreatedDate = now, CreatedBy = request.ActorId, LastModifiedDate = now, LastModifiedBy = request.ActorId,
                };
                location.Events.Add(new ArtifactLocationEvent
                {
                    Id = Guid.NewGuid(), TeamId = request.TeamId, ArtifactLocationId = locationId, Revision = 1,
                    EventType = ArtifactLocationEventType.Verified, State = ArtifactLocationState.Available, ObservedAt = now,
                    ProviderObjectVersion = "v1", ProviderETag = request.ExpectedSha256, ProviderChecksumAlgorithm = "Sha256",
                    ProviderChecksum = digest, ObservedSizeBytes = bytes.Length, VerifiedAt = now, DetailsJson = "{}", CreatedBy = request.ActorId,
                });
                db.ArtifactLocation.Add(location);
                await db.SaveChangesAsync(cancellationToken);
                _bytes[objectId] = bytes;
                _idempotency[request.IdempotencyScope] = new Stored(intentId, objectId, locationId, request.ExpectedSha256);
                return new ArtifactCasTransferResult.Committed(intentId, objectId, locationId, false);
            }
            finally { _gate.Release(); }
        }

        public Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_readProblem != null) return Task.FromResult<ArtifactCasReadResult>(new ArtifactCasReadResult.Unavailable(_readProblem));
            return Task.FromResult<ArtifactCasReadResult>(_bytes.TryGetValue(request.ArtifactObjectId, out var bytes)
                ? new ArtifactCasReadResult.Opened(new MemoryStream(bytes, writable: false), bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))
                : new ArtifactCasReadResult.Unavailable(new ArtifactCasProblem(ArtifactCasProblemCode.TargetMissing, false)));
        }

        public void Remove(Guid objectId) => _bytes.TryRemove(objectId, out _);
        public void Replace(Guid objectId, byte[] bytes) => _bytes[objectId] = bytes;
        public void FailReads(ArtifactCasProblem? problem) => _readProblem = problem;
        private sealed record Stored(Guid IntentId, Guid ObjectId, Guid LocationId, string Sha256);
    }
}
