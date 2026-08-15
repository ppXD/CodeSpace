using System.Collections.Concurrent;
using System.Security.Cryptography;
using Autofac;
using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
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

        var completed = (await service.CompleteAsync(new AgentRunLogCompleteRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = session, ExpectedRevision = second.Metadata.Revision,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        completed.Metadata.State.ShouldBe(AgentRunLogStreamState.Completed);
        completed.Metadata.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(firstBytes.Concat(secondBytes).ToArray())));
        (await service.AppendAsync(Append(world, opened.Metadata.StreamId, session, 3, completed.Metadata.TotalBytes, [1]), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.StreamTerminal);
    }

    [Fact]
    public async Task Exact_append_retry_is_idempotent_while_gap_overlap_and_changed_bytes_fail_closed()
    {
        var world = await SeedWorldAsync();
        var service = Service(new TestCas(_fixture));
        var session = Guid.NewGuid();
        var stream = (await service.OpenAsync(Open(world, session, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata;
        var request = Append(world, stream.StreamId, session, 1, 0, [1, 2, 3, 4]);
        var first = (await service.AppendAsync(request, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        var retry = (await service.AppendAsync(request, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
        retry.WasExactRetry.ShouldBeTrue();
        retry.Segment.SegmentId.ShouldBe(first.Segment.SegmentId);
        (await service.AppendAsync(request with { Bytes = new byte[] { 9, 9, 9, 9 } }, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.IdempotencyConflict);
        (await service.AppendAsync(request with { ExpectedSegmentOrdinal = 2, ExpectedOffsetBytes = 3 }, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.NonContiguous);
        (await service.AppendAsync(request with { ExpectedSegmentOrdinal = 3, ExpectedOffsetBytes = 4 }, CancellationToken.None))
            .ShouldBeOfType<AgentRunLogAppendResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.NonContiguous);
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
                if (_idempotency.TryGetValue(request.IdempotencyKey, out var prior))
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
                _idempotency[request.IdempotencyKey] = new Stored(intentId, objectId, locationId, request.ExpectedSha256);
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
