using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity — the production container's own scope wiring, a real <see cref="RunRecordLogger"/> and real
/// Postgres. The grading heartbeat (<see cref="SupervisorTurnService.RunGradingHeartbeatLoopAsync"/>) runs
/// CONCURRENTLY with the grade it protects, and one DI scope holds ONE <see cref="CodeSpaceDbContext"/>: the turn
/// service's own reads and writes (the unit grade's manifest stamps, the judge's recorded model calls) and every
/// <see cref="IRunRecordLogger"/> resolved from that scope write through the same instance. A pulse that fell due
/// while a grade query was in flight on it died on EF's "a second operation was started on this context" guard —
/// and, awaited in the grade's <c>finally</c>, replaced the grade with that error.
///
/// <para>The test holds a real query open on the grade scope's context (blocked on an advisory lock only the test
/// releases), lets a pulse fall due, and releases only once that pulse has settled — so the verdict is decided by
/// which context the pulse wrote through, never by how the runner scheduled it. The wall clock decides only WHEN
/// the first pulse fires; every wait on it is bounded and names what it watched.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorGradingHeartbeatIsolationFlowTests
{
    private const string NodeId = "sup";
    private const string Graded = "graded while a pulse fell due";

    /// <summary>The advisory-lock class this test's key lives under — a two-key lock never contends with the single-key <c>pg_advisory_xact_lock</c> the run-record admission trigger takes per run.</summary>
    private const int LockClass = 1_976_1994;

    private static readonly TimeSpan PulseInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(30);

    private readonly PostgresFixture _fixture;

    public SupervisorGradingHeartbeatIsolationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_pulse_that_falls_due_mid_query_lands_on_its_own_context_and_the_grade_survives()
    {
        var runId = await SeedRunAsync();
        var lockKey = Random.Shared.Next();

        using var gradeScope = _fixture.BeginScope();
        var service = gradeScope.Resolve<SupervisorTurnService>();

        await using var holder = await HoldLockAsync(lockKey);

        // The grade's own DB work, in flight on the scope's context for as long as the holder keeps the lock.
        var gradeQuery = gradeScope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, {1})", LockClass, lockKey);

        // The premise, at the DI level: a record logger from the grade's own scope writes through the grade's own
        // context, so a write on it collides with the query above. Without this collision the test proves nothing.
        var collision = await Should.ThrowAsync<InvalidOperationException>(() => gradeScope.Resolve<IRunRecordLogger>().LogAsync(runId, NodeId, LogLevel.Info, "premise probe", CancellationToken.None));
        collision.Message.ShouldContain("second operation was started on this context", customMessage: "premise: the scope's record logger shares the grade's DbContext — the collision the heartbeat must be kept out of");

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = service.RunGradingHeartbeatLoopAsync(runId, NodeId, PulseInterval, heartbeatCts.Token, TimeProvider.System);

        BenchmarkGrade grade;
        try
        {
            await PulseSettledAsync(runId, heartbeat);

            await holder.DisposeAsync();   // release: the grade's query takes the lock and completes
            await gradeQuery;

            grade = new BenchmarkGrade { Passed = true, Detail = Graded };
        }
        finally
        {
            // The production call sites' own finally shape: whatever the loop ends with is what this await surfaces.
            heartbeatCts.Cancel();

            try { await heartbeat; }
            catch (OperationCanceledException) { }
        }

        grade.Detail.ShouldBe(Graded, "a heartbeat pulse can never replace the grade it protects");

        (await PulseCountAsync(runId)).ShouldBeGreaterThan(0, "a pulse landed while the grade's query still held the scope's context — only a pulse on its OWN context can");
    }

    /// <summary>
    /// Waits until a pulse row for <paramref name="runId"/> has landed, or the loop itself has ended — a loop that
    /// died on its first pulse has settled too, and the call-site <c>finally</c> then surfaces what killed it.
    /// </summary>
    private async Task PulseSettledAsync(Guid runId, Task heartbeat)
    {
        var deadline = DateTime.UtcNow + SettleBound;

        while (DateTime.UtcNow < deadline)
        {
            if (heartbeat.IsCompleted || await PulseCountAsync(runId) > 0) return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"no grading-heartbeat pulse landed for run {runId} within {SettleBound.TotalSeconds}s while the grade's query held its scope's context, and the loop is still running — every pulse is failing. Look for the SupervisorTurnService pulse warnings: a pulse that writes through the grade's own context dies on EF's second-operation guard.");
    }

    private async Task<int> PulseCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId && r.NodeId == NodeId && r.RecordType == WorkflowRunRecordTypes.Log);
    }

    /// <summary>Takes the lock on an unpooled connection of its own, so disposing it ends the session and releases the lock on every path — including a failed assertion.</summary>
    private async Task<NpgsqlConnection> HoldLockAsync(int lockKey)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();

        await using var take = new NpgsqlCommand("SELECT pg_advisory_lock(@class, @key)", connection);
        take.Parameters.AddWithValue("class", LockClass);
        take.Parameters.AddWithValue("key", lockKey);
        await take.ExecuteNonQueryAsync();

        return connection;
    }

    private async Task<Guid> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);

        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = $"grading-heartbeat-{Guid.NewGuid():N}"[..24], Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = Array.Empty<WorkflowActivationInput>(), Enabled = true });

        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }
}
