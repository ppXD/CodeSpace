using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.StorageTestWorker;
using DbUp;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public sealed partial class AgentRunLogCompletionRecoveryAuditTests
{
    [Fact]
    public async Task A_real_pre_0201_v2_stream_keeps_whole_content_sha_after_upgrade()
    {
        await using var legacy = await LegacyLogDatabase.CreateAsync(fixture);
        var world = await SeedAsync(declareRecovery: false, legacy: legacy);
        using var scope = legacy.BeginScope();
        var probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
        var completed = (await Logs(scope, probe).CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        completed.Metadata.Sha256.ShouldBe(world.WholeSha256);
        completed.Metadata.Integrity.ShouldBeNull();
        AssertReads(probe.Reads.ToArray(), world.ObjectIds);
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.AgentRunLogStream.SingleAsync(value => value.Id == world.Complete.StreamId)).SchemaVersion.ShouldBe(2);
        (await db.AgentRunLogVerification.CountAsync()).ShouldBe(0);
        (await db.AgentRunLogSegment.Where(value => value.StreamId == world.Complete.StreamId).Select(value => value.SchemaVersion).Distinct().SingleAsync()).ShouldBe(2);
    }

    // This database is genuinely created with the old schema. No trigger disabling, backdating or production
    // caller-select-version escape hatch is involved. Only its own database is dropped by this helper.
    private sealed class LegacyLogDatabase(PostgresFixture fixture, string connectionString, string databaseName) : IAsyncDisposable
    {
        public static async Task<LegacyLogDatabase> CreateAsync(PostgresFixture fixture)
        {
            var name = $"codespace_log_upgrade_{Guid.NewGuid():N}";
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await command.ExecuteNonQueryAsync();
            var database = new LegacyLogDatabase(fixture, connection, name);
            try
            {
                var scripts = Path.Combine(Path.GetDirectoryName(typeof(DbUpRunner).Assembly.Location)!, DbUpRunner.ScriptFolder);
                var upgrade = DeployChanges.To.PostgresqlDatabase(connection)
                    .WithScriptsFromFileSystem(scripts, path => string.CompareOrdinal(Path.GetFileName(path)[..4], "0201") < 0)
                    .WithVariablesDisabled().WithTransaction().Build().PerformUpgrade();
                if (!upgrade.Successful) throw upgrade.Error;
                return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }

        public ILifetimeScope BeginScope() => fixture.BeginScope(builder => builder.RegisterInstance(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options).As<DbContextOptions<CodeSpaceDbContext>>());

        public async Task InsertStreamAndUpgradeAsync(AgentRunLogOpenRequest request)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO agent_run_log_stream
                    (id, team_id, agent_run_id, worker_fence_epoch, capture_session_id, stream_kind, content_type, content_encoding,
                     capture_source, retention, state, revision, segment_count, total_bytes, next_segment_ordinal, next_offset_bytes,
                     source_offset_bytes, capture_source_base_offset_bytes, schema_version, created_at, last_modified_at)
                VALUES (@id, @team, @run, @fence, @session, @kind, @type, @encoding, @source, 'Run', 'Open', 1, 0, 0, 1, 0, 0, 0, 2, clock_timestamp(), clock_timestamp())
                """, connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("team", request.TeamId);
            command.Parameters.AddWithValue("run", request.AgentRunId);
            command.Parameters.AddWithValue("fence", request.WorkerFenceEpoch);
            command.Parameters.AddWithValue("session", request.CaptureSessionId);
            command.Parameters.AddWithValue("kind", request.StreamKind);
            command.Parameters.AddWithValue("type", request.ContentType);
            command.Parameters.AddWithValue("encoding", request.ContentEncoding!);
            command.Parameters.AddWithValue("source", request.CaptureSource);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
            new DbUpRunner(connectionString).Run();
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }
}
