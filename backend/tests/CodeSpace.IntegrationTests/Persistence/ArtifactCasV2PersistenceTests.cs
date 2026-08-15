using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof for Wave 3's additive artifact CAS: tenant-bound references, binary content identity,
/// mandatory append-only location history, monotonic transfer saga and one-way immutable run references.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactCasV2PersistenceTests
{
    private readonly PostgresFixture _fixture;

    public ArtifactCasV2PersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exact_object_location_event_transfer_and_run_reference_round_trip()
    {
        var world = await SeedWorldAsync();
        var artifact = Object(world.TeamId, 17, 0x11);
        var location = AvailableLocation(world, artifact, "objects/11/report.md");
        var reference = Reference(world, artifact, "output.primary", "reports/report.md");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactObject.Add(artifact);
            db.ArtifactLocation.Add(location);
            db.ArtifactLocationEvent.Add(Event(location, 1, ArtifactLocationEventType.Verified, ArtifactLocationState.Available));
            db.WorkflowRunArtifactReference.Add(reference);
            await db.SaveChangesAsync();
        }

        var transfer = Transfer(world, artifact, location, "transfer-round-trip");
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactTransferIntent.Add(transfer);
            await db.SaveChangesAsync();
        }

        await AdvanceTransferAsync(transfer.Id, ArtifactTransferState.Uploading, 2);
        await AdvanceTransferAsync(transfer.Id, ArtifactTransferState.Uploaded, 3);
        await AdvanceTransferAsync(transfer.Id, ArtifactTransferState.Verifying, 4);
        await AdvanceTransferAsync(transfer.Id, ArtifactTransferState.Committed, 5, artifact.Id, location.Id);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactObject.Include(o => o.Locations).SingleAsync(o => o.Id == artifact.Id);
            stored.Digest.ShouldBe(Digest(0x11));
            stored.SizeBytes.ShouldBe(17);
            stored.Locations.ShouldHaveSingleItem().StorageProfileRevisionId.ShouldBe(world.StorageProfileRevisionId);

            var storedTransfer = await db.ArtifactTransferIntent.SingleAsync(i => i.Id == transfer.Id);
            storedTransfer.State.ShouldBe(ArtifactTransferState.Committed);
            storedTransfer.Revision.ShouldBe(5);
            storedTransfer.ArtifactObjectId.ShouldBe(artifact.Id);
            storedTransfer.ArtifactLocationId.ShouldBe(location.Id);

            var storedReference = await db.WorkflowRunArtifactReference.SingleAsync(r => r.Id == reference.Id);
            storedReference.WorkflowRunId.ShouldBe(world.WorkflowRunId);
            storedReference.WorkPlanId.ShouldBe(world.WorkPlanId);
            storedReference.PlanVersion.ShouldBe(1);
            storedReference.WorkUnitId.ShouldBe("write-report");
            storedReference.ArtifactObjectId.ShouldBe(artifact.Id);
        }
    }

    [Fact]
    public async Task Object_and_event_are_immutable_and_all_cross_team_links_fail_closed()
    {
        var world = await SeedWorldAsync();
        var other = await SeedWorldAsync();
        var artifact = Object(world.TeamId, 5, 0x22);
        var location = AvailableLocation(world, artifact, "objects/22/file.bin");
        var locationEvent = Event(location, 1, ArtifactLocationEventType.Verified, ArtifactLocationState.Available);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AddRange(artifact, location, locationEvent);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactObject.SingleAsync(o => o.Id == artifact.Id);
            stored.SizeBytes++;
            var mutation = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            mutation.InnerException?.Message.ShouldContain("immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactObject.Add(Object(world.TeamId, 99, 0x22));
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactLocationEvent.SingleAsync(e => e.Id == locationEvent.Id);
            stored.DetailsJson = "{\"rewritten\":true}";
            var mutation = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            mutation.InnerException?.Message.ShouldContain("append-only");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var crossTeam = AvailableLocation(other, artifact, "objects/cross-team.bin");
            db.ArtifactLocation.Add(crossTeam);
            db.ArtifactLocationEvent.Add(Event(crossTeam, 1, ArtifactLocationEventType.Verified, ArtifactLocationState.Available));
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var crossRun = Reference(other, artifact, "output.primary", "cross-team.bin");
            crossRun.WorkPlanId = null;
            crossRun.PlanVersion = null;
            crossRun.WorkUnitId = null;
            crossRun.WorkUnitContractHash = null;
            crossRun.RequirementRevision = null;
            db.WorkflowRunArtifactReference.Add(crossRun);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Every_location_revision_requires_a_matching_event_and_verified_size()
    {
        var world = await SeedWorldAsync();
        var artifact = Object(world.TeamId, 23, 0x33);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactObject.Add(artifact);
            await db.SaveChangesAsync();
        }

        var unverified = AvailableLocation(world, artifact, "objects/33/unverified.bin");
        unverified.ProviderChecksumAlgorithm = null;
        unverified.ProviderChecksum = null;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactLocation.Add(unverified);
            db.ArtifactLocationEvent.Add(Event(unverified, 1, ArtifactLocationEventType.Verified, ArtifactLocationState.Available));
            var weakIdentity = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            weakIdentity.InnerException?.Message.ShouldContain("Available requires exact Sha256");
        }

        var location = PendingLocation(world, artifact, "objects/33/pending.bin");
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactLocation.Add(location);
            var missingEvent = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            missingEvent.InnerException?.Message.ShouldContain("requires matching append-only event");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactLocation.Add(location);
            db.ArtifactLocationEvent.Add(Event(location, 1, ArtifactLocationEventType.Created, ArtifactLocationState.Pending));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactLocation.SingleAsync(l => l.Id == location.Id);
            stored.State = ArtifactLocationState.Available;
            stored.Revision = 2;
            stored.VerifiedAt = DateTimeOffset.UtcNow;
            stored.ObservedSizeBytes = 22;
            stored.ProviderChecksumAlgorithm = "Sha256";
            stored.ProviderChecksum = artifact.Digest;
            db.ArtifactLocationEvent.Add(Event(stored, 2, ArtifactLocationEventType.Verified, ArtifactLocationState.Available));
            var mismatch = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            mismatch.InnerException?.Message.ShouldContain("size does not match");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactLocation.SingleAsync(l => l.Id == location.Id);
            stored.State = ArtifactLocationState.Available;
            stored.Revision = 2;
            stored.VerifiedAt = DateTimeOffset.UtcNow;
            stored.ObservedSizeBytes = artifact.SizeBytes;
            stored.ProviderChecksumAlgorithm = "Sha256";
            stored.ProviderChecksum = artifact.Digest;
            db.ArtifactLocationEvent.Add(Event(stored, 2, ArtifactLocationEventType.Verified, ArtifactLocationState.Available));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactLocation.SingleAsync(l => l.Id == location.Id);
            stored.State = ArtifactLocationState.Missing;
            stored.Revision = 3;
            stored.VerifiedAt = null;
            stored.ObservedSizeBytes = null;
            stored.ProviderObjectVersion = null;
            stored.ProviderETag = null;
            stored.ProviderChecksumAlgorithm = null;
            stored.ProviderChecksum = null;
            var staleEvent = Event(stored, 3, ArtifactLocationEventType.StateChanged, ArtifactLocationState.Missing);
            staleEvent.ProviderETag = "stale-etag";
            db.ArtifactLocationEvent.Add(staleEvent);
            var mismatchedEvent = await db.SaveChangesAsync().ShouldThrowAsync<PostgresException>();
            mismatchedEvent.Message.ShouldContain("matching append-only event snapshot");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactLocation.SingleAsync(l => l.Id == location.Id);
            stored.State = ArtifactLocationState.Missing;
            stored.Revision = 4;
            stored.VerifiedAt = null;
            stored.ObservedSizeBytes = null;
            var skipped = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            skipped.InnerException?.Message.ShouldContain("advance exactly once");
        }
    }

    [Fact]
    public async Task Transfer_and_reference_state_machines_reject_ghost_identity_illegal_transition_and_rewrite()
    {
        var world = await SeedWorldAsync();
        var artifact = Object(world.TeamId, 7, 0x44);
        var location = AvailableLocation(world, artifact, "objects/44/final.bin");
        var reference = Reference(world, artifact, "output.primary", "final.bin");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AddRange(artifact, location, Event(location, 1, ArtifactLocationEventType.Verified, ArtifactLocationState.Available), reference);
            await db.SaveChangesAsync();
        }

        var ghostPlan = Reference(world, artifact, "output.ghost-plan", "ghost-plan.bin");
        ghostPlan.PlanVersion = null;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunArtifactReference.Add(ghostPlan);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        var ghost = Transfer(world, artifact, location, "ghost-attempt");
        ghost.ExecutionAttemptOrdinal = null;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactTransferIntent.Add(ghost);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        var transfer = Transfer(world, artifact, location, "monotonic-transfer");
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.ArtifactTransferIntent.Add(transfer);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.ArtifactTransferIntent.SingleAsync(i => i.Id == transfer.Id);
            stored.State = ArtifactTransferState.Committed;
            stored.Revision = 2;
            stored.ArtifactObjectId = artifact.Id;
            stored.ArtifactLocationId = location.Id;
            stored.CompletedAt = DateTimeOffset.UtcNow;
            var illegal = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            illegal.InnerException?.Message.ShouldContain("illegal state transition");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunArtifactReference.SingleAsync(r => r.Id == reference.Id);
            stored.Role = "output.rewritten";
            var immutable = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            immutable.InnerException?.Message.ShouldContain("stable facts are immutable");
        }

        var superseding = Reference(world, artifact, reference.Role, reference.LogicalPath);
        superseding.ExecutionAttemptId = Guid.NewGuid();
        superseding.ExecutionAttemptOrdinal = 2;
        superseding.ExecutionGeneration = 2;
        superseding.CreatedDate = reference.CreatedDate.AddSeconds(1);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunArtifactReference.Add(superseding);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunArtifactReference.SingleAsync(r => r.Id == reference.Id);
            stored.SupersededByReferenceId = superseding.Id;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunArtifactReference.SingleAsync(r => r.Id == reference.Id);
            stored.SupersededByReferenceId = Guid.NewGuid();
            var second = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            second.InnerException?.Message.ShouldContain("one-way");
        }
    }

    private async Task AdvanceTransferAsync(Guid id, ArtifactTransferState state, long revision, Guid? objectId = null, Guid? locationId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var transfer = await db.ArtifactTransferIntent.SingleAsync(i => i.Id == id);
        transfer.State = state;
        transfer.Revision = revision;
        if (state == ArtifactTransferState.Committed)
        {
            transfer.ArtifactObjectId = objectId;
            transfer.ArtifactLocationId = locationId;
            transfer.CompletedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private async Task<World> SeedWorldAsync()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profileRevisionId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = userId, Email = $"artifact-{userId:N}@test.local", Name = $"artifact-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"artifact-{teamId:N}", Name = "Artifact Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"cas-{Guid.NewGuid():N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = userId,
            LastModifiedDate = now, LastModifiedBy = userId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = profileRevisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfigJson = "{\"rootPath\":\"/srv/codespace/artifacts\"}",
            NamespaceFingerprint = $"sha256:{new string('a', 64)}", CreatedDate = now, CreatedBy = userId,
        });
        db.StorageProfile.Add(profile);

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual,
            Status = WorkflowRunStatus.Running, CreatedBy = userId, LastModifiedBy = userId,
        });
        db.WorkPlan.Add(new WorkPlan
        {
            Id = planId, TeamId = teamId, WorkflowRunId = runId, Version = 1, Status = "Authored",
            OriginKind = "test", Goal = "produce report", ItemsJson = "[]", CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new World(teamId, userId, profileRevisionId, runId, planId);
    }

    private static ArtifactObject Object(Guid teamId, long size, byte digestByte) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
        Digest = Digest(digestByte), SizeBytes = size, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = SystemUsers.SeederId,
    };

    private static ArtifactLocation PendingLocation(World world, ArtifactObject artifact, string objectKey) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = artifact.Id,
        StorageProfileRevisionId = world.StorageProfileRevisionId, Locator = $"storage://primary/{objectKey}",
        ObjectKey = objectKey, State = ArtifactLocationState.Pending, Revision = 1,
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
        LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = world.ActorId,
    };

    private static ArtifactLocation AvailableLocation(World world, ArtifactObject artifact, string objectKey)
    {
        var location = PendingLocation(world, artifact, objectKey);
        location.State = ArtifactLocationState.Available;
        location.ProviderObjectVersion = "v1";
        location.ProviderETag = "etag-1";
        location.ProviderChecksumAlgorithm = "Sha256";
        location.ProviderChecksum = artifact.Digest;
        location.ObservedSizeBytes = artifact.SizeBytes;
        location.VerifiedAt = DateTimeOffset.UtcNow;
        return location;
    }

    private static ArtifactLocationEvent Event(ArtifactLocation location, long revision, ArtifactLocationEventType eventType, ArtifactLocationState state) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = revision,
        EventType = eventType, State = state, ObservedAt = DateTimeOffset.UtcNow,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        DetailsJson = "{}", CreatedBy = location.LastModifiedBy,
    };

    private static ArtifactTransferIntent Transfer(World world, ArtifactObject artifact, ArtifactLocation location, string idempotencyKey) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, StorageProfileRevisionId = world.StorageProfileRevisionId,
        IdempotencyKey = idempotencyKey, ExpectedDigestAlgorithm = artifact.DigestAlgorithm,
        ExpectedDigest = artifact.Digest, ExpectedSizeBytes = artifact.SizeBytes, TargetLocator = location.Locator,
        TargetObjectKey = location.ObjectKey, State = ArtifactTransferState.Intended, Revision = 1,
        ExecutionAttemptId = Guid.NewGuid(), ExecutionAttemptOrdinal = 1, ExecutionGeneration = 1, WorkerFenceEpoch = 1,
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
        LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = world.ActorId,
    };

    private static WorkflowRunArtifactReference Reference(World world, ArtifactObject artifact, string role, string path) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.WorkflowRunId, NodeId = "artifact.capture",
        IterationKey = "", WorkPlanId = world.WorkPlanId, PlanVersion = 1, WorkUnitId = "write-report",
        WorkUnitContractHash = $"sha256:{new string('b', 64)}", RequirementRevision = 1,
        ExecutionAttemptId = Guid.NewGuid(), ExecutionAttemptOrdinal = 1, ExecutionGeneration = 1,
        Role = role, LogicalPath = path, ContentType = "application/octet-stream", Required = true,
        Retention = ArtifactRetention.Run, ArtifactObjectId = artifact.Id,
        CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
    };

    private static byte[] Digest(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileRevisionId, Guid WorkflowRunId, Guid WorkPlanId);
}
