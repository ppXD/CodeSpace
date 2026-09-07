using System.Data.Common;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.StorageTestWorker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public sealed partial class AgentRunLogCompletionRecoveryAuditTests
{
    [Theory]
    [InlineData("team", AgentRunLogProblemCode.Missing)]
    [InlineData("run", AgentRunLogProblemCode.Missing)]
    [InlineData("session", AgentRunLogProblemCode.CaptureClaimConflict)]
    [InlineData("worker", AgentRunLogProblemCode.StaleWorker)]
    [InlineData("revision", AgentRunLogProblemCode.ConcurrentMutation)]
    public async Task Verification_rejects_wrong_scope_before_any_physical_read(string mismatch, AgentRunLogProblemCode expected)
    {
        var world = await SeedAsync(declareRecovery: false);
        var request = mismatch switch
        {
            "team" => world.Complete with { TeamId = Guid.NewGuid() },
            "run" => world.Complete with { AgentRunId = Guid.NewGuid() },
            "session" => world.Complete with { CaptureSessionId = Guid.NewGuid() },
            "worker" => world.Complete with { WorkerFenceEpoch = 8 },
            _ => world.Complete with { ExpectedRevision = world.Complete.ExpectedRevision + 1 },
        };
        using var scope = fixture.BeginScope();
        var probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
        var rejected = (await Logs(scope, probe).CompleteAsync(request, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Rejected>();
        rejected.Problem.Code.ShouldBe(expected);
        probe.Reads.ShouldBeEmpty();
        (await scope.Resolve<CodeSpaceDbContext>().AgentRunLogVerification.AnyAsync(value => value.StreamId == world.Complete.StreamId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Lost_checkpoint_commit_ack_resumes_from_the_durable_database_cursor()
    {
        var world = await SeedAsync(declareRecovery: false);
        using var scope = fixture.BeginScope();
        var lost = new LoseCheckpointAck(fixture.ConnectionString, world.Complete.StreamId);
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(lost).Options;
        var first = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
        await Should.ThrowAsync<IOException>(async () => await new AgentRunLogService(options, first, TimeProvider.System).CompleteAsync(world.Complete, CancellationToken.None));
        lost.Dropped.ShouldBeTrue();
        first.Reads.Select(value => value.ArtifactObjectId).ShouldBe(world.ObjectIds.Take(1));
        using var resumedScope = fixture.BeginScope();
        var resumed = new LogCompletionReadProbe(resumedScope.Resolve<IArtifactCasRuntimeCoordinator>());
        var result = (await Logs(resumedScope, resumed).CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        AssertManifest(world, result.Metadata);
        resumed.Reads.Select(value => value.ArtifactObjectId).ShouldBe(world.ObjectIds.Skip(1));
    }

    [Theory]
    [InlineData("close", AgentRunLogProblemCode.BackendUnavailable)]
    [InlineData("short", AgentRunLogProblemCode.ArtifactCorrupt)]
    [InlineData("long", AgentRunLogProblemCode.ArtifactCorrupt)]
    [InlineData("changed-bytes", AgentRunLogProblemCode.ArtifactCorrupt)]
    public async Task Only_full_verified_eof_and_successful_close_can_advance_a_checkpoint(string fault, AgentRunLogProblemCode expected)
    {
        var world = await SeedAsync(declareRecovery: false, segmentCount: 1, segmentBytes: 4096);
        using var scope = fixture.BeginScope();
        var faulty = new ManifestReadFault(scope.Resolve<IArtifactCasRuntimeCoordinator>(), fault);
        var rejected = (await Logs(scope, faulty).CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Rejected>();
        rejected.Problem.Code.ShouldBe(expected);
        var checkpoint = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogVerification.AsNoTracking().SingleAsync(value => value.StreamId == world.Complete.StreamId);
        checkpoint.NextSegmentOrdinal.ShouldBe(1);
        checkpoint.VerifiedBytes.ShouldBe(0);
        checkpoint.SealedAt.ShouldBeNull();
        faulty.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_manifest_does_not_claim_a_previously_verified_object_is_still_physically_readable(bool corrupt)
    {
        var world = await SeedAsync(declareRecovery: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await CancelAfterPrefixAsync(world, deadline.Token);
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var placement = await (from location in db.ArtifactLocation.AsNoTracking()
                               join revision in db.StorageProfileRevision.AsNoTracking() on location.StorageProfileRevisionId equals revision.Id
                               where location.TeamId == world.Complete.TeamId && location.ArtifactObjectId == world.ObjectIds[0]
                               select new { location.ObjectKey, revision.NonSecretConfigJson }).SingleAsync();
        using var config = JsonDocument.Parse(placement.NonSecretConfigJson);
        var path = Path.Combine(config.RootElement.GetProperty("rootPath").GetString()!, "objects", placement.ObjectKey);
        if (corrupt)
        {
            await using var physical = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            physical.WriteByte((byte)'z');
            await physical.FlushAsync(deadline.Token);
        }
        else File.Delete(path);
        var logs = Logs(scope, scope.Resolve<IArtifactCasRuntimeCoordinator>());
        var completed = (await logs.CompleteAsync(world.Complete, deadline.Token)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        AssertManifest(world, completed.Metadata);
        var currentRead = (await logs.ReadRangeAsync(new AgentRunLogRangeRequest(world.Complete.TeamId, world.Complete.StreamId, 0, 1), deadline.Token)).ShouldBeOfType<AgentRunLogRangeResult.Unavailable>();
        currentRead.Problem.Code.ShouldBe(corrupt ? AgentRunLogProblemCode.ArtifactCorrupt : AgentRunLogProblemCode.ArtifactMissing);
    }

    [Fact]
    public async Task Database_refuses_a_skipped_checkpoint_and_old_binary_recovery_claim()
    {
        var world = await SeedAsync(declareRecovery: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await CancelAfterPrefixAsync(world, deadline.Token);
        await MarkTerminalAsync(world);
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var skip = await Should.ThrowAsync<PostgresException>(async () => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_verification SET next_segment_ordinal = next_segment_ordinal + 2, revision = revision + 1, last_modified_at = clock_timestamp() WHERE stream_id = {world.Complete.StreamId}"));
        skip.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        var owner = Guid.NewGuid();
        var oldClaim = await Should.ThrowAsync<PostgresException>(async () => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {owner}, recovery_fence_epoch = recovery_fence_epoch + 1, recovery_attempt_count = recovery_attempt_count + 1, recovery_started_at = clock_timestamp(), recovery_lease_expires_at = clock_timestamp() + interval '30 seconds', revision = revision + 1, last_modified_at = clock_timestamp() WHERE team_id = {world.Complete.TeamId} AND agent_run_id = {world.Complete.AgentRunId}"));
        oldClaim.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        (await IntentAsync(world)).RecoveryOwnerId.ShouldBeNull();
        (await db.AgentRunLogVerification.AsNoTracking().SingleAsync(value => value.StreamId == world.Complete.StreamId)).NextSegmentOrdinal.ShouldBe(3);
    }

    private sealed class LoseCheckpointAck(string connectionString, Guid streamId) : DbTransactionInterceptor
    {
        public bool Dropped { get; private set; }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Dropped) return;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT next_segment_ordinal FROM agent_run_log_verification WHERE stream_id = @stream", connection);
            command.Parameters.AddWithValue("stream", streamId);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long ordinal || ordinal != 2) return;
            Dropped = true;
            throw new IOException("Injected lost acknowledgement AFTER the real checkpoint transaction committed.");
        }
    }

    private sealed class ManifestReadFault(IArtifactCasRuntimeCoordinator inner, string fault) : IArtifactCasRuntimeCoordinator
    {
        public bool Disposed { get; private set; }
        public Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken) => inner.PutAsync(request, cancellationToken);
        public async Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.OpenReadAsync(request, cancellationToken);
            return result is ArtifactCasReadResult.Opened opened ? opened with { Content = new FaultyManifestStream(opened.Content, fault, () => Disposed = true) } : result;
        }
    }

    private sealed class FaultyManifestStream(Stream inner, string fault, Action disposed) : Stream
    {
        private bool _read;
        private bool _extended;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fault == "short" && _read) return 0;
            var read = await inner.ReadAsync(fault == "short" ? buffer[..Math.Min(buffer.Length, 10)] : buffer, cancellationToken);
            _read = true;
            if (read > 0 && fault == "changed-bytes") buffer.Span[0] ^= 1;
            if (read == 0 && fault == "long" && !_extended) { _extended = true; buffer.Span[0] = 1; return 1; }
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); disposed(); if (fault == "close") throw new IOException("Injected close failure after the real stream was closed."); GC.SuppressFinalize(this); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
