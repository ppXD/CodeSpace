using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.StorageTestWorker;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public sealed partial class AgentRunLogCompletionRecoveryAuditTests
{
    private static void AssertManifest(World world, AgentRunLogMetadata metadata)
    {
        metadata.Sha256.ShouldBeNull();
        metadata.Integrity.ShouldNotBeNull();
        metadata.Integrity.Kind.ShouldBe("segment-manifest-sha256-chain/v1");
        metadata.Integrity.VerifiedBytes.ShouldBe((long)world.SegmentSize * world.Segments);
        metadata.Integrity.VerifiedSegmentCount.ShouldBe(world.Segments);
        metadata.Integrity.VerifiedAt.ShouldBe(metadata.CompletedAt);
        using var canonical = new MemoryStream();
        void Domain(string kind) { canonical.SetLength(0); canonical.Write(Encoding.ASCII.GetBytes($"codespace.agent-run-log.manifest/{kind}/v1\0")); }
        void Number(long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); canonical.Write(bytes); }
        void Id(Guid value) => canonical.Write(value.ToByteArray(bigEndian: true));
        Domain("header");
        Id(world.Complete.TeamId);
        Id(world.Complete.AgentRunId);
        Id(world.Complete.StreamId);
        Number(world.Complete.WorkerFenceEpoch);
        Id(world.Complete.CaptureSessionId);
        Number(world.Complete.ExpectedRevision);
        Number(world.Segments);
        Number((long)world.SegmentSize * world.Segments);
        Number((long)world.SegmentSize * world.Segments);
        var root = SHA256.HashData(canonical.ToArray());
        var segment = new byte[world.SegmentSize];
        for (var index = 0; index < world.Segments; index++)
        {
            Domain("entry");
            canonical.Write(root);
            Number(index + 1);
            Number((long)index * world.SegmentSize);
            Number(world.SegmentSize);
            Id(world.ObjectIds[index]);
            segment.AsSpan().Fill((byte)('a' + index));
            canonical.Write(SHA256.HashData(segment));
            root = SHA256.HashData(canonical.ToArray());
        }
        Domain("seal");
        canonical.Write(root);
        Number(world.Segments);
        Number((long)world.SegmentSize * world.Segments);
        metadata.Integrity.ManifestDigest.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray())));
    }

    [Fact]
    public async Task New_streams_activate_v3_and_old_whole_sha_completion_is_database_fenced()
    {
        var world = await SeedAsync(declareRecovery: false);
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stream = await db.AgentRunLogStream.AsNoTracking().SingleAsync(value => value.Id == world.Complete.StreamId);
        stream.SchemaVersion.ShouldBe(3);

        var legacy = await Should.ThrowAsync<PostgresException>(async () => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_stream SET state = 'Completed', content_digest_algorithm = 'Sha256', content_digest = {Convert.FromHexString(world.WholeSha256)}, revision = revision + 1, completed_at = clock_timestamp(), last_modified_at = clock_timestamp() WHERE id = {world.Complete.StreamId}"));
        legacy.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        (await db.AgentRunLogStream.AsNoTracking().SingleAsync(value => value.Id == world.Complete.StreamId)).State.ShouldBe(Core.Persistence.Entities.AgentRunLogStreamState.Open);
    }

    [Fact]
    public async Task New_manifest_integrity_never_uses_the_whole_sha_field()
    {
        var world = await SeedAsync(declareRecovery: false);
        using var scope = fixture.BeginScope();
        var logs = Logs(scope, scope.Resolve<IArtifactCasRuntimeCoordinator>());
        var result = (await logs.CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        result.Metadata.Sha256.ShouldBeNull("manifest digest is a different identity from whole concatenated content SHA-256");
        var wire = JsonSerializer.SerializeToElement(result.Metadata);
        wire.GetProperty("Integrity").GetProperty("Kind").GetString().ShouldBe("segment-manifest-sha256-chain/v1");
        wire.GetProperty("Integrity").GetProperty("ManifestDigest").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task More_than_eight_verification_steps_make_durable_progress_without_exhausting_recovery()
    {
        var world = await SeedAsync(declareRecovery: true, segmentCount: 72, segmentBytes: 4096);
        await MarkTerminalAsync(world);
        var allReads = new List<Guid>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        for (var step = 0; step < 12; step++)
        {
            using var scope = fixture.BeginScope();
            var probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
            var recovery = Recovery(scope, Logs(scope, probe));
            AgentRunLogCaptureRecoverySummary summary;
            do
            {
                summary = await recovery.ReconcileAsync(deadline.Token);
                if (summary.Claimed == 0) await Task.Delay(10, deadline.Token);
            } while (summary.Claimed == 0);
            summary.LostLease.ShouldBe(0);
            summary.ExternalStateIndeterminate.ShouldBe(0);
            probe.Reads.Count.ShouldBeLessThanOrEqualTo(8, "one recovery step must not materialize/hash the full log");
            allReads.AddRange(probe.Reads.Select(value => value.ArtifactObjectId));
            if (summary.Completed == 1)
            {
                step.ShouldBeGreaterThanOrEqualTo(8, "the test must actually cross the historical 8-attempt budget");
                allReads.ShouldBe(world.ObjectIds);
                return;
            }
        }
        throw new Xunit.Sdk.XunitException("Healthy progress did not finish after more than eight bounded steps.");
    }
}
