using System.Collections.Concurrent;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The executor, the log capture bridge and everything the bridge reaches are resolved from ONE lifetime scope in
/// production — the Hangfire job's — and the capture loop runs BESIDE the durable runner's drain tick. So any scoped
/// <see cref="CodeSpaceDbContext"/> the capture loop reaches is the tick's own context. The storage driver broker's
/// profile and credential readers were one: a segment append resolving its driver while the tick flushed events or
/// wrote the spool offset put two statements on one context, EF refused the second, and a healthy run (22 minutes of
/// review work on codespace-test) landed Failed with "A second operation was started on this context instance".
///
/// <para>Fidelity: HIGH. The executor is resolved exactly as the job scope resolves it, so the bridge, the log service,
/// the CAS coordinator, the broker and both resolvers are the production graph; the runner is the real LocalProcessRunner
/// and the database real Postgres. Every earlier capture test resolved the bridge from a DIFFERENT scope, which is why
/// none saw this. The overlap is forced, not hoped for: an ACCESS EXCLUSIVE lock on <c>storage_credential</c>, held on a
/// separate connection, keeps the broker's credential read — and on the append path nothing else reads that table —
/// in flight, standing in for a slow round-trip, while the agent keeps printing so every drain tick has work. A
/// <c>pg_stat_activity</c> witness proves the read really was waiting.</para>
/// </summary>
public partial class AgentRunExecutorTests
{
    [Fact]
    public async Task A_capture_append_held_in_flight_does_not_cost_the_run_its_verdict()
    {
        if (OperatingSystem.IsWindows()) return;

        var probe = await RunWithCaptureQueryHeldAsync();

        probe.LockWaiters.ShouldNotBeEmpty("fixture check: the capture's storage_credential read was never seen waiting in Postgres, so this proves nothing");

        using var read = _fixture.BeginScope();
        var db = read.Resolve<CodeSpaceDbContext>();
        var run = await read.Resolve<IAgentRunService>().GetAsync(probe.RunId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"run error: {run.Error}");
        probe.ExecutorLog.Entries.ShouldNotContain(e => e.Exception != null && e.Exception.Message.Contains("A second operation was started"));

        var streams = await db.AgentRunLogStream.AsNoTracking().Where(s => s.AgentRunId == probe.RunId).Select(s => new { s.StreamKind, s.State, s.ErrorCode }).ToListAsync();
        streams.ShouldNotBeEmpty();
        streams.ShouldAllBe(s => s.ErrorCode != "observer-failed-before-terminal");
    }

    [Fact]
    public async Task A_reattached_capture_append_held_in_flight_does_not_cost_the_run_its_verdict()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);
        using var destination = new WritableLogDestination();
        await SeedCredentialedAgentRunLogRouteAsync(teamId, destination.RootPath);

        using var gate = new TempDir();
        var go = Path.Combine(gate.Path, "go");
        var script = $"echo ready; while [ ! -f '{go}' ]; do sleep 0.1; done; seq 1 3000 | awk '{{printf \"burst-%05d-%090d\\n\", $1, 0}}'; for i in $(seq 1 150); do echo tick-$i; sleep 0.1; done";

        SandboxHandle launched;
        using (var seed = _fixture.BeginScope())
        {
            var svc = seed.Resolve<IAgentRunService>();
            await svc.MarkRunningAsync(runId, CancellationToken.None);
            var runner = (CodeSpace.Core.Services.Agents.Sandbox.ISandboxDurableRunner)seed.Resolve<CodeSpace.Core.Services.Agents.Sandbox.ISandboxRunnerRegistry>().Resolve(CodeSpace.Core.Services.Agents.Sandbox.Runners.LocalProcessRunner.LocalKind);
            launched = await runner.LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", script }, TimeoutSeconds = 300 }, runId.ToString("N"), CancellationToken.None);
            await svc.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(launched, AgentJson.Options), CancellationToken.None);
            await seed.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
        }

        AgentRunReattachReservation reservation;
        using (var seed = _fixture.BeginScope())
            reservation = (await seed.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None)).ShouldNotBeNull();

        var executorLog = new RecordingLogger<AgentRunExecutor>();
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance<IAgentHarnessRegistry>(new AgentHarnessRegistry(new IAgentHarness[] { new ScriptedHarness("unused") }));
            b.RegisterInstance<ILogger<AgentRunExecutor>>(executorLog);
        });

        try
        {
            var execution = scope.Resolve<IAgentRunExecutor>().ReattachAsync(reservation, CancellationToken.None);

            await WaitUntilAsync(() => HasLogStream(runId), TimeSpan.FromSeconds(60), "the re-attach never opened a capture session");

            await using var locker = new NpgsqlConnection(_fixture.ConnectionString);
            await locker.OpenAsync();
            await using var lockTx = await locker.BeginTransactionAsync();
            await using (var take = new NpgsqlCommand("LOCK TABLE storage_credential IN ACCESS EXCLUSIVE MODE", locker, lockTx))
                await take.ExecuteNonQueryAsync();

            await File.WriteAllTextAsync(go, "go");

            using var watching = new CancellationTokenSource();
            var waiters = WatchLockWaitersAsync(watching.Token);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
            }
            finally
            {
                await lockTx.RollbackAsync();
                watching.Cancel();
            }

            (await waiters).ShouldNotBeEmpty("fixture check: the capture's storage_credential read was never seen waiting in Postgres, so nothing held the shared context");

            await AwaitWithinAsync(execution, TimeSpan.FromSeconds(90), "the re-attach did not return");

            using var read = _fixture.BeginScope();
            var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
            var result = JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson!, AgentJson.Options).ShouldNotBeNull();

            run.Status.ShouldBe(AgentRunStatus.Succeeded, $"the re-attached run must land its own verdict — it ended {run.Status}: {run.Error}");
            executorLog.Entries.ShouldNotContain(e => e.Exception != null && e.Exception.Message.StartsWith("A second operation was started on this context instance"));

            var streams = await read.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking().Where(s => s.AgentRunId == runId).Select(s => new { s.State, s.ErrorCode }).ToListAsync();
            streams.ShouldNotBeEmpty();
            streams.ShouldAllBe(s => s.ErrorCode != "observer-failed-before-terminal");
        }
        finally
        {
            try { System.Diagnostics.Process.GetProcessById(launched.ProcessId).Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }
    }

    private sealed record HeldCaptureProbe(Guid RunId, RecordingLogger<AgentRunExecutor> ExecutorLog, RecordingLogger<AgentRunLogCaptureBridge> BridgeLog, IReadOnlyList<string> LockWaiters);

    /// <summary>Drive the production-resolved executor over a real agent while the capture's storage_credential read is held in flight by a table lock for a fixed window, then release and wait for the executor to return.</summary>
    private async Task<HeldCaptureProbe> RunWithCaptureQueryHeldAsync()
    {
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        using var destination = new WritableLogDestination();
        await SeedCredentialedAgentRunLogRouteAsync(teamId, destination.RootPath);

        using var gate = new TempDir();
        var go = Path.Combine(gate.Path, "go");

        // Phase 1: one small line — the bridge opens its streams, no segment is due (< 256 KiB).
        // Phase 2 (after "go"): ~300 KiB as 3000 short lines crosses MinimumSegmentBytes, so the capture loop makes a
        //   NON-final append; then a line every 100ms so every drain tick has lines to flush and an offset to write.
        var harness = new ScriptedHarness(
            $"echo ready; while [ ! -f '{go}' ]; do sleep 0.1; done; " +
            "seq 1 3000 | awk '{printf \"burst-%05d-%090d\\n\", $1, 0}'; " +
            "for i in $(seq 1 150); do echo tick-$i; sleep 0.1; done");

        var executorLog = new RecordingLogger<AgentRunExecutor>();
        var bridgeLog = new RecordingLogger<AgentRunLogCaptureBridge>();

        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance<IAgentHarnessRegistry>(new AgentHarnessRegistry(new IAgentHarness[] { harness }));
            b.RegisterInstance<ILogger<AgentRunExecutor>>(executorLog);
            b.RegisterInstance<ILogger<AgentRunLogCaptureBridge>>(bridgeLog);
        });

        var executor = scope.Resolve<IAgentRunExecutor>();
        var execution = executor.ExecuteAsync(runId, CancellationToken.None);

        await WaitUntilAsync(() => HasLogStream(runId), TimeSpan.FromSeconds(60),
            "no agent_run_log_stream row appeared — the capture bridge never opened a session, so there is no capture loop to race (check the seeded route resolves Ready)");

        await using var locker = new NpgsqlConnection(_fixture.ConnectionString);
        await locker.OpenAsync();
        await using var lockTx = await locker.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("LOCK TABLE storage_credential IN ACCESS EXCLUSIVE MODE", locker, lockTx))
            await take.ExecuteNonQueryAsync();

        using var watching = new CancellationTokenSource();
        var waiters = WatchLockWaitersAsync(watching.Token);

        await File.WriteAllTextAsync(go, "go");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
        }
        finally
        {
            await lockTx.RollbackAsync();
            watching.Cancel();
        }

        var seen = await waiters;

        await AwaitWithinAsync(execution, TimeSpan.FromSeconds(90), "the executor did not return after the lock was released");

        return new HeldCaptureProbe(runId, executorLog, bridgeLog, seen);
    }

    /// <summary>Independent witness that the capture's shared-context query really was IN FLIGHT: a Postgres backend waiting on a lock with a storage_credential query.</summary>
    private async Task<IReadOnlyList<string>> WatchLockWaitersAsync(CancellationToken stop)
    {
        var seen = new List<string>();
        await using var watcher = new NpgsqlConnection(_fixture.ConnectionString);
        await watcher.OpenAsync();

        while (!stop.IsCancellationRequested)
        {
            await using (var cmd = new NpgsqlCommand("SELECT left(regexp_replace(query, '\\s+', ' ', 'g'), 200) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%storage_credential%' AND pid <> pg_backend_pid()", watcher))
            await using (var reader = await cmd.ExecuteReaderAsync())
                while (await reader.ReadAsync()) seen.Add(reader.GetString(0));

            try { await Task.Delay(20, stop); } catch (OperationCanceledException) { break; }
        }

        return seen;
    }

    private AgentRunStatus RunStatusOf(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).Single();
    }

    /// <summary>An Active agent-run-log route over a local-rwx profile whose revision carries a database credential reference, so every segment append's broker resolution reads storage_credential on the executor's scoped context (as an OSS profile does in production).</summary>
    private async Task SeedCredentialedAgentRunLogRouteAsync(Guid teamId, string rootPath)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));
        var canonicalConfig = CodeSpace.Core.Services.Workflows.Artifacts.Profiles.StorageProfileRules.CanonicalJson(document.RootElement);
        using var canonical = JsonDocument.Parse(canonicalConfig);

        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"probe-log-dest-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = canonicalConfig, CredentialRef = $"db:{Guid.NewGuid():D}:1",
            NamespaceFingerprint = CodeSpace.Core.Services.Workflows.Artifacts.Profiles.StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, canonical.RootElement),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        db.StorageProfile.Add(profile);

        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = AgentRunLogStorageResolver.DataClassTypeKey,
            CurrentRevision = 1, State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = 1,
            StorageProfileId = profileId, ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite,
            CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        db.StorageRoute.Add(route);
        await db.SaveChangesAsync();

        route.State = StorageRouteState.Active;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
