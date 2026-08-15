using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that Agent Run log metadata cannot outrun its bytes, worker fence or contiguous stream head.
/// The feature remains schema-only: no existing runner, completion authority or harness consumes these rows.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunLogPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Verified_segment_advances_the_head_and_stream_can_complete_without_rewriting_bytes()
    {
        var world = await SeedWorldAsync();
        var artifact = await SeedArtifactAsync(world, 17, available: true);
        var stream = await SeedStreamAsync(world);
        var segment = Segment(world, stream, artifact, fenceEpoch: 7, ordinal: 1, offset: 0);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRunLogSegment.Add(segment);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.AgentRunLogStream.SingleAsync(candidate => candidate.Id == stream.Id);
            stored.Revision.ShouldBe(2);
            stored.SegmentCount.ShouldBe(1);
            stored.TotalBytes.ShouldBe(17);
            stored.NextSegmentOrdinal.ShouldBe(2);
            stored.NextOffsetBytes.ShouldBe(17);

            var completedAt = DateTimeOffset.UtcNow;
            stored.State = AgentRunLogStreamState.Completed;
            stored.Revision++;
            stored.ContentDigestAlgorithm = ArtifactDigestAlgorithm.Sha256;
            stored.ContentDigest = Enumerable.Repeat((byte)17, 32).ToArray();
            stored.CompletedAt = completedAt;
            stored.LastModifiedAt = completedAt;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.AgentRunLogStream.SingleAsync(candidate => candidate.Id == stream.Id);
            stored.State.ShouldBe(AgentRunLogStreamState.Completed);
            stored.Revision.ShouldBe(3);
            stored.SegmentCount.ShouldBe(1);
            stored.TotalBytes.ShouldBe(17);
            (await db.AgentRunLogSegment.SingleAsync(candidate => candidate.Id == segment.Id)).ArtifactObjectId.ShouldBe(artifact.Id);
        }
    }

    [Fact]
    public async Task Stale_fence_and_non_contiguous_ranges_are_rejected_before_the_head_moves()
    {
        var world = await SeedWorldAsync();
        var artifact = await SeedArtifactAsync(world, 11, available: true);
        var stream = await SeedStreamAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRunLogSegment.Add(Segment(world, stream, artifact, fenceEpoch: 6, ordinal: 1, offset: 0));
            var stale = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            stale.InnerException?.Message.ShouldContain("stale worker fence rejected");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRunLogSegment.Add(Segment(world, stream, artifact, fenceEpoch: 7, ordinal: 2, offset: 11));
            var gap = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            gap.InnerException?.Message.ShouldContain("locked next ordinal/offset/schema");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.AgentRunLogStream.SingleAsync(candidate => candidate.Id == stream.Id);
            stored.Revision.ShouldBe(1);
            stored.SegmentCount.ShouldBe(0);
            stored.TotalBytes.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Segment_requires_verified_available_bytes_with_the_exact_object_length()
    {
        var world = await SeedWorldAsync();
        var artifact = await SeedArtifactAsync(world, 13, available: false);
        var stream = await SeedStreamAsync(world);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRunLogSegment.Add(Segment(world, stream, artifact, fenceEpoch: 7, ordinal: 1, offset: 0));
        var unavailable = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        unavailable.InnerException?.Message.ShouldContain("CAS bytes are not verified Available");
    }

    [Fact]
    public async Task Higher_current_worker_fence_can_reclaim_an_open_stream_but_stale_or_same_fence_session_replacement_is_rejected()
    {
        var world = await SeedWorldAsync();
        var stream = await SeedStreamAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.AgentRunLogStream.SingleAsync(value => value.Id == stream.Id);
            stored.CaptureSessionId = Guid.NewGuid();
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var sameFence = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            sameFence.InnerException?.Message.ShouldContain("stale or malformed capture claim rejected");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await db.AgentRun.Where(value => value.TeamId == world.TeamId && value.Id == world.AgentRunId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.FenceEpoch, 8));
            var stored = await db.AgentRunLogStream.SingleAsync(value => value.Id == stream.Id);
            stored.WorkerFenceEpoch = 8;
            stored.CaptureSessionId = Guid.NewGuid();
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var stored = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.SingleAsync(value => value.Id == stream.Id);
            stored.WorkerFenceEpoch.ShouldBe(8);
            stored.Revision.ShouldBe(2);
            stored.SegmentCount.ShouldBe(0);
            stored.TotalBytes.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Segment_capture_session_must_match_the_current_stream_claim()
    {
        var world = await SeedWorldAsync();
        var artifact = await SeedArtifactAsync(world, 9, available: true);
        var stream = await SeedStreamAsync(world);
        var segment = Segment(world, stream, artifact, fenceEpoch: 7, ordinal: 1, offset: 0);
        segment.CaptureSessionId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRunLogSegment.Add(segment);
        var mismatch = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        mismatch.InnerException?.Message.ShouldContain("capture claim mismatch rejected");
    }

    [Fact]
    public async Task Terminal_stream_and_existing_segments_are_immutable()
    {
        var world = await SeedWorldAsync();
        var artifact = await SeedArtifactAsync(world, 7, available: true);
        var stream = await SeedStreamAsync(world);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.AgentRunLogStream.SingleAsync(candidate => candidate.Id == stream.Id);
            var completedAt = DateTimeOffset.UtcNow;
            stored.State = AgentRunLogStreamState.CaptureFailed;
            stored.Revision++;
            stored.CompletedAt = completedAt;
            stored.LastModifiedAt = completedAt;
            stored.ErrorCode = "artifact.backend-unavailable";
            stored.ErrorMessage = "capture could not reach the configured backend";
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRunLogSegment.Add(Segment(world, stream, artifact, fenceEpoch: 7, ordinal: 1, offset: 0));
            var append = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            append.InnerException?.Message.ShouldContain("requires its open tenant-bound stream");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.AgentRunLogStream.SingleAsync(candidate => candidate.Id == stream.Id);
            stored.State = AgentRunLogStreamState.Open;
            stored.Revision++;
            stored.CompletedAt = null;
            stored.ErrorCode = null;
            stored.ErrorMessage = null;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var reopen = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            reopen.InnerException?.Message.ShouldContain("terminal state is immutable");
        }
    }

    [Fact]
    public async Task Runtime_completed_stream_requires_a_non_null_sha256_digest()
    {
        var world = await SeedWorldAsync();
        var stream = await SeedStreamAsync(world);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.AgentRunLogStream.SingleAsync(candidate => candidate.Id == stream.Id);
        stored.State = AgentRunLogStreamState.Completed;
        stored.Revision++;
        stored.CompletedAt = DateTimeOffset.UtcNow;
        stored.LastModifiedAt = stored.CompletedAt.Value;
        var missingDigest = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        missingDigest.InnerException?.Message.ShouldContain("requires its verified SHA-256 content digest");
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profileRevisionId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"agent-log-{actorId:N}@test.local", Name = $"agent-log-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-log-{teamId:N}", Name = "Agent Log Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"agent-log-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId,
            LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = profileRevisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = "{\"rootPath\":\"/srv/codespace/artifacts\"}",
            NamespaceFingerprint = $"sha256:{new string('a', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running,
            TaskJson = "{}", FenceEpoch = 7, CreatedDate = now, CreatedBy = actorId,
            LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return new World(teamId, actorId, profileRevisionId, agentRunId);
    }

    private async Task<ArtifactObject> SeedArtifactAsync(World world, long size, bool available)
    {
        var artifact = new ArtifactObject
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
            Digest = Enumerable.Repeat((byte)size, 32).ToArray(), SizeBytes = size,
            CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ArtifactObject.Add(artifact);
        if (available)
        {
            var now = DateTimeOffset.UtcNow;
            var location = new ArtifactLocation
            {
                Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = artifact.Id,
                StorageProfileRevisionId = world.StorageProfileRevisionId, Locator = $"storage://agent-logs/{artifact.Id:N}",
                ObjectKey = $"agent-logs/{artifact.Id:N}", ProviderObjectVersion = "v1", ProviderETag = $"etag-{artifact.Id:N}",
                ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = artifact.Digest, ObservedSizeBytes = size,
                State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = now,
                CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
            };
            db.ArtifactLocation.Add(location);
            db.ArtifactLocationEvent.Add(new ArtifactLocationEvent
            {
                Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactLocationId = location.Id, Revision = 1,
                EventType = ArtifactLocationEventType.Verified, State = ArtifactLocationState.Available, ObservedAt = now,
                ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
                ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
                ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
                ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
                DetailsJson = "{}", CreatedBy = world.ActorId,
            });
        }
        await db.SaveChangesAsync();
        return artifact;
    }

    private async Task<AgentRunLogStream> SeedStreamAsync(World world)
    {
        var now = DateTimeOffset.UtcNow;
        var stream = new AgentRunLogStream
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId,
            WorkerFenceEpoch = 7, CaptureSessionId = Guid.NewGuid(),
            StreamKind = "stdout/v1", ContentType = "text/plain", ContentEncoding = "utf-8",
            CaptureSource = "sandbox-spool/v1", Retention = ArtifactRetention.Run,
            State = AgentRunLogStreamState.Open, Revision = 1, SegmentCount = 0, TotalBytes = 0,
            NextSegmentOrdinal = 1, NextOffsetBytes = 0, SchemaVersion = 2,
            CreatedAt = now, LastModifiedAt = now,
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRunLogStream.Add(stream);
        await db.SaveChangesAsync();
        return stream;
    }

    private static AgentRunLogSegment Segment(World world, AgentRunLogStream stream, ArtifactObject artifact, long fenceEpoch, long ordinal, long offset)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentRunLogSegment
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = stream.Id,
            SegmentOrdinal = ordinal, StartOffsetBytes = offset, LengthBytes = artifact.SizeBytes,
            ArtifactObjectId = artifact.Id, WorkerFenceEpoch = fenceEpoch, CaptureSessionId = stream.CaptureSessionId!.Value,
            FirstObservedAt = now, LastObservedAt = now, CreatedAt = now, SchemaVersion = 2,
        };
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileRevisionId, Guid AgentRunId);
}
