using System.Data;
using System.Data.Common;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactManifestAtomicityFlowTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_server_failure_before_the_successor_insert_preserves_current_and_the_same_scope_can_retry()
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        var fault = new RefuseManifestInsert();
        using var scope = Scope(fault);
        var store = scope.Resolve<IArtifactManifestStore>();

        var failure = await Should.ThrowAsync<Exception>(() => CaptureAsync(store, workspace, scenario));

        PostgresErrorOf(failure).SqlState.ShouldBe(PostgresErrorCodes.DivisionByZero);
        fault.Refusals.ShouldBe(1);
        await AssertCurrentAsync(scenario, "v1", expectedRows: 1);
        await CaptureAsync(store, workspace, scenario);
        await AssertCurrentAsync(scenario, "v2", expectedRows: 2);
    }

    [Fact]
    public async Task A_reader_never_observes_a_missing_current_between_retiring_and_installing_the_successor()
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        var gate = new ManifestInsertGate();
        using var writer = Scope(gate);
        var capture = CaptureAsync(writer.Resolve<IArtifactManifestStore>(), workspace, scenario);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try { await AssertCurrentAsync(scenario, "v1", expectedRows: 1); }
        finally { gate.Release.TrySetResult(); }

        await capture.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertCurrentAsync(scenario, "v2", expectedRows: 2);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Concurrent_captures_serialize_the_current_pointer_and_deduplicate_identical_bytes(bool sameBytes, bool existingCurrent)
    {
        var scenario = await SeedAsync(existingCurrent);
        using var firstWorkspace = new Workspace("v2");
        using var secondWorkspace = new Workspace(sameBytes ? "v2" : "v3");
        var gate = new ManifestInsertGate();
        using var firstScope = Scope(gate);
        using var secondScope = Scope();
        var first = CaptureAsync(firstScope.Resolve<IArtifactManifestStore>(), firstWorkspace, scenario);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondDb = secondScope.Resolve<CodeSpaceDbContext>();
        await secondDb.Database.OpenConnectionAsync();
        var secondPid = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        var second = CaptureAsync(secondScope.Resolve<IArtifactManifestStore>(), secondWorkspace, scenario);

        try { await WaitForBlockedOrFinishedAsync(second, secondPid); }
        finally { gate.Release.TrySetResult(); }

        await System.Threading.Tasks.Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        await AssertCurrentAsync(scenario, sameBytes ? "v2" : "v3", (existingCurrent ? 1 : 0) + (sameBytes ? 1 : 2));
    }

    [Fact]
    public async Task A_caller_transaction_can_commit_other_work_after_a_failed_manifest_savepoint()
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        var fault = new RefuseManifestInsert();
        using var scope = Scope(fault);
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var failure = await Should.ThrowAsync<Exception>(() => CaptureAsync(scope.Resolve<IArtifactManifestStore>(), workspace, scenario));

        PostgresErrorOf(failure).SqlState.ShouldBe(PostgresErrorCodes.DivisionByZero);
        db.Database.CurrentTransaction.ShouldBeSameAs(transaction, "the store must not commit or replace the caller's transaction");
        await db.Team.Where(team => team.Id == scenario.TeamId).ExecuteUpdateAsync(set => set.SetProperty(team => team.Name, "outer transaction survived"));
        await transaction.CommitAsync();
        await AssertCurrentAsync(scenario, "v1", expectedRows: 1);
        using var reader = Scope();
        (await reader.Resolve<CodeSpaceDbContext>().Team.SingleAsync(team => team.Id == scenario.TeamId)).Name.ShouldBe("outer transaction survived");
    }

    [Fact]
    public async Task A_successful_recapture_does_not_commit_the_callers_transaction()
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        using var scope = Scope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await CaptureAsync(scope.Resolve<IArtifactManifestStore>(), workspace, scenario);

        db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        await AssertCurrentAsync(scenario, "v1", expectedRows: 1);
        await transaction.RollbackAsync();
        await AssertCurrentAsync(scenario, "v1", expectedRows: 1);
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task A_stale_caller_snapshot_cannot_report_identical_content_as_current(IsolationLevel isolation)
    {
        var scenario = await SeedAsync();
        using var staleWorkspace = new Workspace("v1");
        using var newerWorkspace = new Workspace("v2");
        using var staleScope = Scope();
        var db = staleScope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(isolation);
        (await db.ArtifactManifest.AsNoTracking().SingleAsync(row => row.AgentRunId == scenario.AgentRunId)).SupersededByManifestId.ShouldBeNull();
        using (var newerScope = Scope()) await CaptureAsync(newerScope.Resolve<IArtifactManifestStore>(), newerWorkspace, scenario);

        var failure = await Should.ThrowAsync<Exception>(() => CaptureAsync(staleScope.Resolve<IArtifactManifestStore>(), staleWorkspace, scenario));

        PostgresErrorOf(failure).SqlState.ShouldBe(PostgresErrorCodes.SerializationFailure);
        await transaction.RollbackAsync();
        await AssertCurrentAsync(scenario, "v2", expectedRows: 2);
    }

    [Fact]
    public async Task Terminating_the_database_backend_between_retire_and_insert_preserves_current()
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        var gate = new ManifestInsertGate();
        using var writer = Scope(gate);
        var db = writer.Resolve<CodeSpaceDbContext>();
        await db.Database.OpenConnectionAsync();
        var processId = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        var capture = CaptureAsync(writer.Resolve<IArtifactManifestStore>(), workspace, scenario);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
            terminate.Parameters.AddWithValue("pid", processId);
            (await terminate.ExecuteScalarAsync()).ShouldBe(true);
        }
        finally { gate.Release.TrySetResult(); }

        var failure = await Should.ThrowAsync<Exception>(() => capture.WaitAsync(TimeSpan.FromSeconds(10)));
        var databaseFailure = failure is DbUpdateException update ? update.InnerException : failure;
        databaseFailure.ShouldBeAssignableTo<NpgsqlException>();
        await AssertCurrentAsync(scenario, "v1", expectedRows: 1);
        using var retry = Scope();
        await CaptureAsync(retry.Resolve<IArtifactManifestStore>(), workspace, scenario);
        await AssertCurrentAsync(scenario, "v2", expectedRows: 2);
    }

    [Fact]
    public async Task Losing_the_application_receipt_after_database_commit_is_safe_to_retry()
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        var gate = new ManifestInsertGate();
        var receipt = new LoseCommitReceipt();
        using var scope = Scope(gate, receipt);
        var store = scope.Resolve<IArtifactManifestStore>();
        var capture = CaptureAsync(store, workspace, scenario);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        receipt.Armed = true;
        gate.Release.TrySetResult();

        var failure = await Should.ThrowAsync<IOException>(() => capture.WaitAsync(TimeSpan.FromSeconds(10)));

        failure.Message.ShouldBe("manifest commit receipt lost");
        receipt.LostReceipts.ShouldBe(1);
        await AssertCurrentAsync(scenario, "v2", expectedRows: 2);
        await CaptureAsync(store, workspace, scenario);
        await AssertCurrentAsync(scenario, "v2", expectedRows: 2);
    }

    [Fact]
    public async Task Public_reads_see_new_current_without_rewriting_the_callers_tracked_entity()
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        using var scope = Scope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var original = await db.ArtifactManifest.SingleAsync(row => row.AgentRunId == scenario.AgentRunId);
        var store = scope.Resolve<IArtifactManifestStore>();

        await CaptureAsync(store, workspace, scenario);

        var rows = await store.ListForAgentRunAsync(scenario.AgentRunId, scenario.TeamId, CancellationToken.None);
        rows.Count.ShouldBe(2);
        var current = rows.Where(row => row.SupersededByManifestId == null).ShouldHaveSingleItem();
        rows.Single(row => row.Id == original.Id).SupersededByManifestId.ShouldBe(current.Id);
        original.SupersededByManifestId.ShouldBeNull("set-based writes do not refresh previously tracked snapshots");
        db.Entry(original).State.ShouldBe(EntityState.Unchanged);
        await db.Entry(original).ReloadAsync();
        original.SupersededByManifestId.ShouldBe(current.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_manifest_phase_preserves_unrelated_pending_changes_on_success_and_failure(bool failInsert)
    {
        var scenario = await SeedAsync();
        using var workspace = new Workspace("v2");
        var pending = new Team { Id = Guid.NewGuid(), Slug = "manifest-pending-" + Guid.NewGuid().ToString("N"), Name = "caller-owned pending change" };
        var pendingChange = new AddPendingChangeAtManifestRead(pending);
        using var scope = failInsert ? Scope(pendingChange, new RefuseManifestInsert()) : Scope(pendingChange);
        var store = scope.Resolve<IArtifactManifestStore>();

        if (failInsert)
            PostgresErrorOf(await Should.ThrowAsync<Exception>(() => CaptureAsync(store, workspace, scenario))).SqlState.ShouldBe(PostgresErrorCodes.DivisionByZero);
        else
            await CaptureAsync(store, workspace, scenario);

        pendingChange.Additions.ShouldBe(1, "pending work is injected after retention has saved its own declaration");
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.Entry(pending).State.ShouldBe(EntityState.Added);
        pending.Name.ShouldBe("caller-owned pending change");
        pending.CreatedDate.ShouldBe(default);
        using var reader = Scope();
        (await reader.Resolve<CodeSpaceDbContext>().Team.AnyAsync(team => team.Id == pending.Id)).ShouldBeFalse();
        await AssertCurrentAsync(scenario, failInsert ? "v1" : "v2", failInsert ? 1 : 2);
    }

    [Fact]
    public async Task Publication_preserves_creation_audit_and_records_the_current_actor_on_replacement()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var scenario = new Scenario(teamId, Guid.NewGuid());
        using var firstWorkspace = new Workspace("v1");
        using var secondWorkspace = new Workspace("v2");
        using (var firstScope = Scope()) await CaptureAsync(firstScope.Resolve<IArtifactManifestStore>(), firstWorkspace, scenario);
        using var reader = Scope();
        var db = reader.Resolve<CodeSpaceDbContext>();
        var original = await db.ArtifactManifest.AsNoTracking().SingleAsync(row => row.AgentRunId == scenario.AgentRunId);
        original.CreatedBy.ShouldBe(SystemUsers.SeederId);
        original.LastModifiedBy.ShouldBe(SystemUsers.SeederId);
        original.CreatedDate.ShouldNotBe(default);
        original.LastModifiedDate.ShouldBe(original.CreatedDate);

        using (var actorScope = fixture.BeginScopeAs(actorId, teamId)) await CaptureAsync(actorScope.Resolve<IArtifactManifestStore>(), secondWorkspace, scenario);

        var rows = await db.ArtifactManifest.AsNoTracking().Where(row => row.AgentRunId == scenario.AgentRunId).ToListAsync();
        var prior = rows.Single(row => row.Id == original.Id);
        prior.CreatedBy.ShouldBe(original.CreatedBy);
        prior.CreatedDate.ShouldBe(original.CreatedDate);
        prior.LastModifiedBy.ShouldBe(actorId);
        prior.LastModifiedDate.ShouldBeGreaterThanOrEqualTo(original.LastModifiedDate);
        var fresh = rows.Where(row => row.SupersededByManifestId == null).ShouldHaveSingleItem();
        fresh.CreatedBy.ShouldBe(actorId);
        fresh.LastModifiedBy.ShouldBe(actorId);
        fresh.CreatedDate.ShouldBe(prior.LastModifiedDate);
        fresh.LastModifiedDate.ShouldBe(fresh.CreatedDate);
    }

    private async Task<Scenario> SeedAsync(bool captureInitial = true)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var scenario = new Scenario(teamId, Guid.NewGuid());
        if (captureInitial)
        {
            using var workspace = new Workspace("v1");
            using var scope = Scope();
            await CaptureAsync(scope.Resolve<IArtifactManifestStore>(), workspace, scenario);
        }
        return scenario;
    }

    private ILifetimeScope Scope(params IInterceptor[] interceptors) => interceptors.Length == 0 ? fixture.BeginScope() : fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptors).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private static Task<int> CaptureAsync(IArtifactManifestStore store, Workspace workspace, Scenario scenario) => store.CaptureDeclaredAsync(
        new AgentTask { Goal = "capture report", Harness = "codex-cli", Acceptance = new SupervisorAcceptanceSpec { Kind = BenchmarkGradingKind.ArtifactPresent, Command = ["report.md"] } },
        workspace.Path, scenario.AgentRunId, null, scenario.TeamId, 1, CancellationToken.None);

    private async Task AssertCurrentAsync(Scenario scenario, string content, int expectedRows)
    {
        using var scope = Scope();
        var rows = await scope.Resolve<CodeSpaceDbContext>().ArtifactManifest.AsNoTracking().Where(row => row.AgentRunId == scenario.AgentRunId).ToListAsync();
        rows.Count.ShouldBe(expectedRows);
        var current = rows.Where(row => row.SupersededByManifestId is null).ShouldHaveSingleItem();
        var stored = await scope.Resolve<IArtifactStore>().GetBytesAsync(scenario.TeamId, current.ContentArtifactId, CancellationToken.None);
        Encoding.UTF8.GetString(stored.ShouldNotBeNull().Bytes).ShouldBe(content);
        current.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))));
        var visited = new HashSet<Guid>();
        var predecessor = rows.Single(row => !rows.Any(other => other.SupersededByManifestId == row.Id));
        while (true)
        {
            visited.Add(predecessor.Id).ShouldBeTrue("supersession must form one acyclic history");
            if (predecessor.SupersededByManifestId is not { } successor) break;
            predecessor = rows.Single(row => row.Id == successor);
        }
        visited.Count.ShouldBe(rows.Count);
        predecessor.Id.ShouldBe(current.Id);
    }

    private async Task WaitForBlockedOrFinishedAsync(Task capture, int processId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!capture.IsCompleted)
        {
            await using var command = new NpgsqlCommand("SELECT wait_event = 'advisory' FROM pg_stat_activity WHERE pid = @pid", connection);
            command.Parameters.AddWithValue("pid", processId);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await System.Threading.Tasks.Task.Delay(10, deadline.Token);
        }
    }

    private static PostgresException PostgresErrorOf(Exception error)
    {
        // EF's query execution strategy wraps transient PostgreSQL errors; the server SQLSTATE remains decisive.
        while (error is not PostgresException && error.InnerException is { } inner) error = inner;
        return error.ShouldBeOfType<PostgresException>();
    }

    private sealed class RefuseManifestInsert : DbCommandInterceptor
    {
        public int Refusals { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await RefuseAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RefuseAsync(command, cancellationToken);
            return result;
        }

        private async Task RefuseAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (Refusals != 0 || !IsManifestInsert(command)) return;
            Refusals++;
            await using var poison = command.Connection!.CreateCommand();
            poison.Transaction = command.Transaction;
            poison.CommandText = "SELECT 1 / 0";
            await poison.ExecuteScalarAsync(cancellationToken);
        }
    }

    private sealed class ManifestInsertGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await PauseAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await PauseAsync(command, cancellationToken);
            return result;
        }

        private async Task PauseAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (IsManifestInsert(command) && Entered.TrySetResult()) await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class AddPendingChangeAtManifestRead(Team pending) : DbCommandInterceptor
    {
        public int Additions { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Additions == 0 && command.CommandText.Contains("FROM artifact_manifest", StringComparison.OrdinalIgnoreCase))
            {
                Additions++;
                eventData.Context!.Add(pending);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LoseCommitReceipt : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public int LostReceipts { get; private set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Armed || LostReceipts != 0) return System.Threading.Tasks.Task.CompletedTask;
            LostReceipts++;
            throw new IOException("manifest commit receipt lost");
        }
    }

    private static bool IsManifestInsert(DbCommand command) => command.CommandText.Contains("INSERT INTO artifact_manifest", StringComparison.OrdinalIgnoreCase);

    private sealed record Scenario(Guid TeamId, Guid AgentRunId);

    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codespace-manifest-atomic-" + Guid.NewGuid().ToString("N"));

        public Workspace(string content)
        {
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "report.md"), content);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
