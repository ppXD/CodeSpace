using System.Collections;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.StorageTestWorker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public sealed partial class AgentRunLogCompletionRecoveryAuditTests
{
    [Fact]
    public async Task Segment_metadata_is_actually_read_in_bounded_pages_not_only_declared_in_sql()
    {
        var world = await SeedAsync(declareRecovery: false, segmentCount: 72, segmentBytes: 4096);
        using var scope = fixture.BeginScope();
        var counter = new VerificationRowCounter();
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(counter).Options;
        var physical = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
        var result = (await new AgentRunLogService(options, physical, TimeProvider.System).CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Progress>();
        result.VerifiedSegments.ShouldBe(8);
        counter.Pages.Where(value => value.Kind == "segments").Select(value => value.Rows).ShouldBe(new[] { 8 });
        counter.Pages.Where(value => value.Kind == "locations").Count().ShouldBe(8);
        counter.Pages.ShouldAllBe(value => value.Disposed && value.ReachedEof);
        physical.Reads.Count.ShouldBe(8);
        physical.Reads.ShouldAllBe(value => value.MaximumRequestedBytes <= 128 * 1024 && value.EofCount == 1 && value.Disposed);
    }

    [Fact]
    public async Task Recorded_location_search_reads_real_bounded_pages_past_nine_missing_physical_copies()
    {
        var world = await SeedAsync(declareRecovery: false, segmentCount: 1, segmentBytes: 4096);
        for (var index = 0; index < 9; index++) await AddMissingCopyAsync(world);
        using var scope = fixture.BeginScope();
        var counter = new VerificationRowCounter();
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(counter).Options;
        var result = (await new AgentRunLogService(options, scope.Resolve<IArtifactCasRuntimeCoordinator>(), TimeProvider.System).CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        AssertManifest(world, result.Metadata);
        counter.Pages.Where(value => value.Kind == "locations").Select(value => value.Rows).ShouldBe(new[] { 4, 4, 2 });
        counter.Pages.ShouldAllBe(value => value.Disposed && value.ReachedEof);
    }

    private async Task AddMissingCopyAsync(World world)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-log-location-page-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var actor = await db.TeamMembership.Where(value => value.TeamId == world.Complete.TeamId).Select(value => value.UserId).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = new StorageProfile { Id = Guid.NewGuid(), TeamId = world.Complete.TeamId, StableName = $"page-{Guid.NewGuid():N}", State = StorageProfileState.Active, CurrentRevision = 1, CreatedDate = now, CreatedBy = actor, LastModifiedDate = now, LastModifiedBy = actor };
        var config = JsonSerializer.SerializeToElement(new { rootPath = root });
        profile.Revisions.Add(new StorageProfileRevision { Id = Guid.NewGuid(), TeamId = world.Complete.TeamId, StorageProfileId = profile.Id, Revision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = config.GetRawText(), NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, config), CreatedDate = now, CreatedBy = actor });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        var bytes = new byte[world.SegmentSize];
        bytes.AsSpan().Fill((byte)'a');
        const string key = "missing-copy/log-segment";
        await using var content = new MemoryStream(bytes, writable: false);
        var written = (await scope.Resolve<IArtifactCasRuntimeCoordinator>().PutAsync(new ArtifactCasTransferRequest
        {
            TeamId = world.Complete.TeamId, StorageProfileId = profile.Id, StorageProfileRevision = 1, ActorId = actor,
            IdempotencyScope = $"location-page/{profile.Id:N}", TargetObjectKey = key, Content = content,
            ExpectedSizeBytes = bytes.Length, ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), ContentType = "text/plain",
        }, CancellationToken.None)).ShouldBeOfType<ArtifactCasTransferResult.Committed>();
        written.ArtifactObjectId.ShouldBe(world.ObjectIds[0]);
        var path = Path.Combine(root, "objects", key);
        File.Exists(path).ShouldBeTrue();
        File.Delete(path);
        File.Exists(path).ShouldBeFalse();
    }

    // Count returned physical DbDataReader rows, not SQL text limits or mocked query results. The interceptor is
    // attached only to the log consumer's options; the actual CAS coordinator still uses its production options.
    private sealed class VerificationRowCounter : DbCommandInterceptor
    {
        public ConcurrentQueue<ReadPage> Pages { get; } = new();
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            var kind = command.CommandText.Contains("FROM agent_run_log_segment ", StringComparison.Ordinal) ? "segments"
                : command.CommandText.Contains("FROM artifact_location ", StringComparison.Ordinal) ? "locations" : null;
            if (kind == null) return ValueTask.FromResult(result);
            var page = new ReadPage(kind);
            Pages.Enqueue(page);
            return ValueTask.FromResult<DbDataReader>(new CountedReader(result, page));
        }
    }

    private sealed class ReadPage(string kind)
    {
        public string Kind { get; } = kind;
        public int Rows { get; set; }
        public bool ReachedEof { get; set; }
        public bool Disposed { get; set; }
    }

    private sealed class CountedReader(DbDataReader inner, ReadPage page) : DbDataReader
    {
        private bool Count(bool read) { if (read) page.Rows++; else page.ReachedEof = true; return read; }
        public override bool Read() => Count(inner.Read());
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken) => Count(await inner.ReadAsync(cancellationToken));
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
        public override void Close() { inner.Close(); page.Disposed = true; }
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); page.Disposed = true; } }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); page.Disposed = true; GC.SuppressFinalize(this); }
    }
}
