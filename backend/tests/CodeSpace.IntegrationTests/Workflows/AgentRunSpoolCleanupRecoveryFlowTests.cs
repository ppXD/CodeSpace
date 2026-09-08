using System.Data.Common;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunSpoolCleanupRecoveryFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codespace-spool-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _settings;

    public AgentRunSpoolCleanupRecoveryFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _settings = RuntimeSettings.Override(value => value with { AgentRunSpoolDirectory = _root });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("another-host")]
    public async Task An_unknown_or_foreign_host_keeps_its_handle_and_local_resources(string? host)
    {
        var run = await SeedAsync(handle => handle with { LaunchHost = host });

        using var scope = Scope();
        await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);

        File.ReadAllText(Path.Combine(run.Directory, "out.log")).ShouldBe("retained output");
        await AssertHandleAsync(run, present: true);
    }

    [UnixPermissionsFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_real_filesystem_delete_refusal_keeps_the_handle_until_a_later_successful_sweep()
    {
        var run = await SeedAsync();
        var output = Path.Combine(run.Directory, "out.log");
        File.SetUnixFileMode(run.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => File.Delete(output));
            using var scope = Scope();
            await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);
            await AssertHandleAsync(run, present: true);
            await AssertRetryScheduledAsync(run, "filesystem-access");
            File.ReadAllText(output).ShouldBe("retained output");
        }
        finally { File.SetUnixFileMode(run.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        await MakeRetryDueAsync(run);
        using var retry = Scope();
        (await retry.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);
        Directory.Exists(run.Directory).ShouldBeFalse();
        await AssertHandleAsync(run, present: false);
        await AssertRetryClearedAsync(run);
    }

    [UnixPermissionsFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_real_spool_family_enumeration_refusal_keeps_all_cleanup_evidence()
    {
        var run = await SeedAsync();
        var earlierRound = Path.Combine(_root, $"{run.Id:N}-r1");
        Directory.CreateDirectory(earlierRound);
        File.WriteAllText(Path.Combine(earlierRound, "out.log"), "earlier round");
        File.SetUnixFileMode(_root, UnixFileMode.UserExecute);
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => Directory.GetDirectories(_root));
            using var scope = Scope();
            await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);
            await AssertHandleAsync(run, present: true);
            await AssertRetryScheduledAsync(run, "filesystem-access");
        }
        finally { File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        await MakeRetryDueAsync(run);
        using var retry = Scope();
        await retry.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);
        Directory.Exists(run.Directory).ShouldBeFalse();
        Directory.Exists(earlierRound).ShouldBeFalse();
        await AssertHandleAsync(run, present: false);
    }

    [Fact]
    public async Task Foreign_and_legacy_backlogs_do_not_consume_the_local_batch_limit()
    {
        var local = await SeedAsync();
        using (var seed = Scope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            for (var index = 0; index < AgentRunSpoolReaper.BatchSize + 1; index++)
            {
                var handle = JsonSerializer.Deserialize<SandboxHandle>(local.Handle, AgentJson.Options)! with { LaunchHost = index % 2 == 0 ? null : "another-host" };
                db.AgentRun.Add(Row(local.TeamId, Guid.NewGuid(), JsonSerializer.Serialize(handle, AgentJson.Options), DateTimeOffset.UtcNow.AddDays(-3)));
            }
            await db.SaveChangesAsync();
        }

        using var scope = Scope();
        await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);

        await AssertHandleAsync(local, present: false);
        var foreign = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(row => row.TeamId == local.TeamId && row.Id != local.Id).ToListAsync();
        foreign.Count.ShouldBe(AgentRunSpoolReaper.BatchSize + 1);
        foreign.ShouldAllBe(row => row.RunnerHandleJson != null);
    }

    [Fact]
    public async Task A_full_batch_of_capture_held_runs_does_not_starve_a_later_cleanable_run()
    {
        var cleanable = await SeedAsync();
        var blockers = new List<RunSpool>(AgentRunSpoolReaper.BatchSize);
        var now = DateTimeOffset.UtcNow;
        using (var seed = Scope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            for (var index = 0; index < AgentRunSpoolReaper.BatchSize; index++)
            {
                var id = Guid.NewGuid();
                var directory = Path.Combine(_root, id.ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "out.log"), "capture source");
                var handle = JsonSerializer.Serialize(new SandboxHandle
                {
                    Kind = "local", ProcessId = 1, SpoolDirectory = directory, Deadline = now, LaunchHost = LocalProcessRunner.CurrentHost,
                }, AgentJson.Options);
                db.AgentRun.Add(new AgentRun { Id = id, TeamId = cleanable.TeamId, Harness = "codex-cli", Status = AgentRunStatus.Running, FenceEpoch = 1, RunnerHandleJson = handle });
                db.AgentRunLogCaptureIntent.Add(ExpectedCaptureIntent(cleanable.TeamId, id, now));
                blockers.Add(new RunSpool(cleanable.TeamId, id, directory, handle));
            }
            await db.SaveChangesAsync();
            await db.AgentRun.Where(row => row.TeamId == cleanable.TeamId && row.Status == AgentRunStatus.Running)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, AgentRunStatus.Succeeded).SetProperty(row => row.CompletedAt, now.AddDays(-3)));
        }

        using (var scope = Scope())
            (await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        Directory.Exists(cleanable.Directory).ShouldBeFalse("capture-held rows must be excluded before LIMIT so ordinary eligible work remains reachable");
        await AssertHandleAsync(cleanable, present: false);
        blockers.ShouldAllBe(run => Directory.Exists(run.Directory));
        using var evidence = Scope();
        var evidenceDb = evidence.Resolve<CodeSpaceDbContext>();
        var blockerIds = blockers.Select(run => run.Id).ToArray();
        (await evidenceDb.AgentRun.AsNoTracking().CountAsync(row => row.TeamId == cleanable.TeamId && blockerIds.Contains(row.Id) && row.RunnerHandleJson != null))
            .ShouldBe(AgentRunSpoolReaper.BatchSize);
        (await evidenceDb.AgentRunLogCaptureIntent.AsNoTracking().CountAsync(intent => intent.TeamId == cleanable.TeamId && intent.State == AgentRunLogCaptureIntentState.Expected))
            .ShouldBe(AgentRunSpoolReaper.BatchSize);
    }

    [Fact]
    public async Task A_full_batch_of_failed_local_candidates_does_not_starve_a_later_cleanable_run()
    {
        var cleanable = await SeedAsync();
        using (var seed = Scope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            for (var index = 0; index < AgentRunSpoolReaper.BatchSize; index++)
            {
                var id = Guid.NewGuid();
                var invalid = new SandboxHandle
                {
                    Kind = "local",
                    ProcessId = 1,
                    SpoolDirectory = Path.Combine(Path.GetTempPath(), $"outside-spool-{id:N}"),
                    Deadline = DateTimeOffset.UtcNow,
                    LaunchHost = LocalProcessRunner.CurrentHost,
                };
                db.AgentRun.Add(Row(cleanable.TeamId, id, JsonSerializer.Serialize(invalid, AgentJson.Options), DateTimeOffset.UtcNow.AddDays(-3)));
            }
            await db.SaveChangesAsync();
        }

        using (var first = Scope())
            (await first.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None)).ShouldBe(0, "the oldest batch is deliberately uncleanable");

        using (var evidence = Scope())
        {
            var db = evidence.Resolve<CodeSpaceDbContext>();
            var failed = await db.AgentRun.AsNoTracking().Where(row => row.TeamId == cleanable.TeamId && row.Id != cleanable.Id).ToListAsync();
            failed.ShouldAllBe(row => row.RunnerHandleJson != null && row.SpoolCleanupAttempts == 1 && row.SpoolCleanupLastAttemptAt != null
                && row.SpoolCleanupNextAttemptAt > row.SpoolCleanupLastAttemptAt && row.SpoolCleanupLastErrorCode == "invalid-spool-path");
            await db.AgentRun.Where(row => row.TeamId == cleanable.TeamId && row.Id != cleanable.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.SpoolCleanupNextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        using (var second = Scope())
            (await second.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1, "even due failed candidates must yield to less-attempted eligible work");

        Directory.Exists(cleanable.Directory).ShouldBeFalse();
        await AssertHandleAsync(cleanable, present: false);
    }

    [Fact]
    public async Task Writing_a_new_runner_handle_clears_retry_state_from_the_previous_cleanup_obligation()
    {
        var run = await SeedAsync();
        using (var seed = Scope())
            await seed.Resolve<CodeSpaceDbContext>().AgentRun.Where(row => row.Id == run.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.SpoolCleanupAttempts, 7)
                .SetProperty(row => row.SpoolCleanupLastAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))
                .SetProperty(row => row.SpoolCleanupNextAttemptAt, DateTimeOffset.UtcNow.AddHours(2))
                .SetProperty(row => row.SpoolCleanupLastErrorCode, "filesystem-io"));

        var replacement = JsonSerializer.Serialize(JsonSerializer.Deserialize<SandboxHandle>(run.Handle, AgentJson.Options)! with { ProcessId = 42 }, AgentJson.Options);
        using (var writer = Scope())
            await writer.Resolve<IAgentRunService>().SetRunnerHandleAsync(run.Id, replacement, CancellationToken.None);

        using var reader = Scope();
        var actual = await reader.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(row => row.Id == run.Id);
        JsonSerializer.Deserialize<SandboxHandle>(actual.RunnerHandleJson.ShouldNotBeNull(), AgentJson.Options)
            .ShouldBe(JsonSerializer.Deserialize<SandboxHandle>(replacement, AgentJson.Options));
        actual.SpoolCleanupAttempts.ShouldBe(0);
        actual.SpoolCleanupLastAttemptAt.ShouldBeNull();
        actual.SpoolCleanupNextAttemptAt.ShouldBeNull();
        actual.SpoolCleanupLastErrorCode.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_candidate_whose_handle_or_terminal_state_changed_is_not_deleted(bool replaceHandle)
    {
        var run = await SeedAsync();
        var gate = new CandidateReadGate();
        using var scope = Scope(gate);
        var reap = scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var mutation = Scope();
            var db = mutation.Resolve<CodeSpaceDbContext>();
            if (replaceHandle)
            {
                var replacement = JsonSerializer.Deserialize<SandboxHandle>(run.Handle, AgentJson.Options)! with { ProcessId = 42 };
                await db.AgentRun.Where(row => row.Id == run.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RunnerHandleJson, JsonSerializer.Serialize(replacement, AgentJson.Options)));
            }
            else
                await db.AgentRun.Where(row => row.Id == run.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, AgentRunStatus.Running).SetProperty(row => row.CompletedAt, (DateTimeOffset?)null));
        }
        finally { gate.Release.TrySetResult(); }

        await reap.WaitAsync(TimeSpan.FromSeconds(10));
        File.ReadAllText(Path.Combine(run.Directory, "out.log")).ShouldBe("retained output");
        using var reader = Scope();
        var actual = await reader.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(row => row.Id == run.Id);
        actual.RunnerHandleJson.ShouldNotBeNull();
        if (replaceHandle) JsonSerializer.Deserialize<SandboxHandle>(actual.RunnerHandleJson!, AgentJson.Options)!.ProcessId.ShouldBe(42);
        else actual.Status.ShouldBe(AgentRunStatus.Running);
    }

    [Fact]
    public async Task A_capture_admitted_after_candidate_discovery_holds_the_raw_source_before_deletion()
    {
        var run = await SeedAsync();
        DateTimeOffset completedAt;
        using (var reader = Scope())
            completedAt = (await reader.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(row => row.Id == run.Id)).CompletedAt.ShouldNotBeNull();
        var gate = new CandidateReadGate();
        using var scope = Scope(gate);
        var reap = scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var mutation = Scope();
            var db = mutation.Resolve<CodeSpaceDbContext>();
            await db.AgentRun.Where(row => row.Id == run.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, AgentRunStatus.Running).SetProperty(row => row.CompletedAt, (DateTimeOffset?)null));
            var now = DateTimeOffset.UtcNow;
            db.AgentRunLogCaptureIntent.Add(ExpectedCaptureIntent(run.TeamId, run.Id, now));
            await db.SaveChangesAsync();
            await db.AgentRun.Where(row => row.Id == run.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, AgentRunStatus.Succeeded).SetProperty(row => row.CompletedAt, completedAt));
        }
        finally { gate.Release.TrySetResult(); }

        (await reap.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(0);
        File.ReadAllText(Path.Combine(run.Directory, "out.log")).ShouldBe("retained output");
        await AssertHandleAsync(run, present: true);
    }

    [Fact]
    public async Task Two_reapers_can_observe_the_same_candidate_without_losing_cleanup_evidence()
    {
        var run = await SeedAsync();
        var firstGate = new CandidateReadGate();
        var secondGate = new CandidateReadGate();
        using var first = Scope(firstGate);
        using var second = Scope(secondGate);
        var firstReap = first.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);
        var secondReap = second.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);
        try { await System.Threading.Tasks.Task.WhenAll(firstGate.Entered.Task, secondGate.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            firstGate.Release.TrySetResult();
            secondGate.Release.TrySetResult();
        }

        var counts = await System.Threading.Tasks.Task.WhenAll(firstReap, secondReap).WaitAsync(TimeSpan.FromSeconds(10));
        counts.Sum().ShouldBe(1);
        Directory.Exists(run.Directory).ShouldBeFalse();
        await AssertHandleAsync(run, present: false);
    }

    [Fact]
    public async Task A_database_backend_killed_after_deletion_leaves_a_handle_the_next_sweep_can_finish()
    {
        var run = await SeedAsync();
        var fault = new KillBackendBeforeHandleClear(_fixture.ConnectionString, run);
        using var scope = Scope(fault);

        var failure = await Should.ThrowAsync<Exception>(() => scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None));

        fault.Kills.ShouldBe(1);
        while (failure is not NpgsqlException && failure.InnerException is { } inner) failure = inner;
        failure.ShouldBeAssignableTo<NpgsqlException>();
        Directory.Exists(run.Directory).ShouldBeFalse("the real filesystem delete happened before the database failure");
        await AssertHandleAsync(run, present: true);
        using var retry = Scope();
        (await retry.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);
        await AssertHandleAsync(run, present: false);
    }

    private async Task<RunSpool> SeedAsync(Func<SandboxHandle, SandboxHandle>? transform = null)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var id = Guid.NewGuid();
        var directory = Path.Combine(_root, id.ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "out.log"), "retained output");
        var handle = new SandboxHandle { Kind = "local", ProcessId = 1, SpoolDirectory = directory, Deadline = DateTimeOffset.UtcNow, LaunchHost = LocalProcessRunner.CurrentHost };
        var json = JsonSerializer.Serialize(transform?.Invoke(handle) ?? handle, AgentJson.Options);
        using var scope = Scope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(Row(teamId, id, json, DateTimeOffset.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();
        return new RunSpool(teamId, id, directory, json);
    }

    private static AgentRun Row(Guid teamId, Guid id, string handle, DateTimeOffset completedAt) => new()
    {
        Id = id, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Succeeded, FenceEpoch = 1,
        RunnerHandleJson = handle, CompletedAt = completedAt,
    };

    private static AgentRunLogCaptureIntent ExpectedCaptureIntent(Guid teamId, Guid agentRunId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = agentRunId, WorkerFenceEpoch = 1, CaptureSessionId = Guid.NewGuid(),
        StreamKind = "stdout/v1", ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "durable-spool/v1",
        State = AgentRunLogCaptureIntentState.Expected, Revision = 1, NextRecoveryAt = now, CreatedAt = now, LastModifiedAt = now,
    };

    private ILifetimeScope Scope(params IInterceptor[] interceptors) => interceptors.Length == 0 ? _fixture.BeginScope() : _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptors).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private async Task AssertHandleAsync(RunSpool run, bool present)
    {
        using var scope = Scope();
        var handle = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(row => row.Id == run.Id).Select(row => row.RunnerHandleJson).SingleAsync();
        if (present)
            JsonSerializer.Deserialize<SandboxHandle>(handle.ShouldNotBeNull(), AgentJson.Options).ShouldBe(JsonSerializer.Deserialize<SandboxHandle>(run.Handle, AgentJson.Options));
        else handle.ShouldBeNull();
    }

    private async Task AssertRetryScheduledAsync(RunSpool run, string errorCode)
    {
        using var scope = Scope();
        var row = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(value => value.Id == run.Id);
        row.SpoolCleanupAttempts.ShouldBe(1);
        row.SpoolCleanupLastAttemptAt.ShouldNotBeNull();
        row.SpoolCleanupNextAttemptAt.Value.ShouldBeGreaterThan(row.SpoolCleanupLastAttemptAt.Value);
        row.SpoolCleanupLastErrorCode.ShouldBe(errorCode);
    }

    private async Task AssertRetryClearedAsync(RunSpool run)
    {
        using var scope = Scope();
        var row = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(value => value.Id == run.Id);
        row.SpoolCleanupAttempts.ShouldBe(0);
        row.SpoolCleanupLastAttemptAt.ShouldBeNull();
        row.SpoolCleanupNextAttemptAt.ShouldBeNull();
        row.SpoolCleanupLastErrorCode.ShouldBeNull();
    }

    private async Task MakeRetryDueAsync(RunSpool run)
    {
        using var scope = Scope();
        await scope.Resolve<CodeSpaceDbContext>().AgentRun.Where(row => row.Id == run.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.SpoolCleanupNextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    private sealed class CandidateReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("agent_run", StringComparison.Ordinal) && command.CommandText.Contains("LIMIT", StringComparison.Ordinal) && Entered.TrySetResult())
                await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class KillBackendBeforeHandleClear(string connectionString, RunSpool run) : DbCommandInterceptor
    {
        public int Kills { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Kills != 0 || !command.CommandText.Contains("UPDATE agent_run", StringComparison.OrdinalIgnoreCase) || !command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == run.Id)) return result;
            Directory.Exists(run.Directory).ShouldBeFalse();
            var pid = ((NpgsqlConnection)command.Connection!).ProcessID;
            await using var killer = new NpgsqlConnection(connectionString);
            await killer.OpenAsync(cancellationToken);
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", killer);
            terminate.Parameters.AddWithValue("pid", pid);
            (await terminate.ExecuteScalarAsync(cancellationToken)).ShouldBe(true);
            Kills++;
            return result;
        }
    }

    private sealed class UnixPermissionsFactAttribute : FactAttribute
    {
        public UnixPermissionsFactAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "Real Unix mode permission refusal requires macOS or Linux.";
            else if (GetEffectiveUserId() == 0) Skip = "Root bypasses Unix mode permissions; run this filesystem test as an unprivileged user.";
        }

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint GetEffectiveUserId();
    }

    private sealed record RunSpool(Guid TeamId, Guid Id, string Directory, string Handle);

    public void Dispose()
    {
        _settings.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
