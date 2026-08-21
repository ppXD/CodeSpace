using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Tasks.Trace;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves the live ledger tail (the Room SSE relay's source): <see cref="IRunRecordStreamer"/> yields every
/// workflow_run_record row beyond a cursor in Sequence order and STOPS at a terminal run record; it honors the cursor
/// (exclusive — a resuming client never re-receives a row); and it is team-scoped — a foreign team tails nothing.
/// Read-only against the REAL ledger + real team boundary, so a tenancy or ordering regression fails here.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RunRecordStreamerFlowTests
{
    private readonly PostgresFixture _fixture;

    public RunRecordStreamerFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Tails_records_after_the_cursor_in_sequence_order_and_stops_at_a_terminal_run_record()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        var correlationId = Guid.NewGuid();
        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionStarted, "llm1", "llm1#0", correlationId, null, Payload(new { kind = "llm.complete" }), CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "llm1", "llm1#0", correlationId, null, Payload(new { ordinal = 0, text = "hello there" }), CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionCompleted, "llm1", "llm1#0", correlationId, null, Payload(new { output = "hello there" }), CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), outputsPresent: true, CancellationToken.None);
        }

        var records = await TailAsync(teamId, userId, runId, after: 0);

        records.Select(r => r.Sequence).ShouldBeInOrder(SortDirection.Ascending, "the tail yields rows in ledger Sequence order");
        records[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.RunCompleted, "the tail STOPS at the terminal run record — it does not hang");
        records.ShouldContain(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta, "the interaction.delta row is streamed");

        var delta = records.First(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta);
        JsonDocument.Parse(delta.PayloadJson).RootElement.GetProperty("ordinal").GetInt32().ShouldBe(0);
        delta.CorrelationId.ShouldBe(correlationId, "the streamed delta carries its correlation id for the consumer to group by");
    }

    [Fact]
    public async Task Yields_only_records_strictly_after_the_given_cursor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RecordInteractionAsync(runId, WorkflowRunRecordTypes.InteractionDelta, "llm1", "llm1#0", Guid.NewGuid(), null, Payload(new { ordinal = 0 }), CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), true, CancellationToken.None);
        }

        var all = await TailAsync(teamId, userId, runId, after: 0);
        var midCursor = all[all.Count / 2].Sequence;

        var after = await TailAsync(teamId, userId, runId, after: midCursor);

        after.ShouldAllBe(r => r.Sequence > midCursor, "the cursor is EXCLUSIVE — a resuming client never re-receives a row it already saw");
    }

    [Fact]
    public async Task Run_scoped_sequence_is_assigned_after_the_commit_gate_so_a_late_transaction_cannot_land_behind_the_live_cursor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var baseline = await ReadRunCursorAsync(runId);

        await using var firstConnection = new NpgsqlConnection(_fixture.ConnectionString);
        await firstConnection.OpenAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        var firstSequence = await InsertRecordAsync(firstConnection, firstTransaction, runId, "test.first-transaction");

        var applicationName = $"run-record-cursor-{Guid.NewGuid():N}";
        var secondInsert = InsertRecordAndCommitAsync(runId, WorkflowRunRecordTypes.RunCompleted, applicationName);
        var secondWaitsForFirstCommit = await WaitForLockAsync(applicationName, secondInsert);
        if (!secondWaitsForFirstCommit)
        {
            await firstTransaction.RollbackAsync();
            await secondInsert;
        }

        secondWaitsForFirstCommit.ShouldBeTrue(
            "the production BEFORE INSERT trigger must retain the run gate from sequence assignment through transaction commit");

        await firstTransaction.CommitAsync();
        var secondSequence = await secondInsert.WaitAsync(TimeSpan.FromSeconds(10));
        firstSequence.ShouldBeLessThan(secondSequence);

        var streamed = await TailAsync(teamId, userId, runId, baseline);
        streamed.Select(x => x.Sequence).ShouldBe(new[] { firstSequence, secondSequence });
        streamed.Select(x => x.RecordType).ShouldBe(new[] { "test.first-transaction", WorkflowRunRecordTypes.RunCompleted });
    }

    [Fact]
    public async Task Sequence_is_database_owned_even_when_an_EF_caller_supplies_a_value()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId);
        var record = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Sequence = long.MaxValue,
            RecordType = "test.explicit-sequence",
            IterationKey = string.Empty,
            PayloadJson = "{}",
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunRecord.Add(record);
        await db.SaveChangesAsync();

        record.Sequence.ShouldNotBe(long.MaxValue, "the tracked entity must receive the trigger-assigned cursor rather than retaining a caller-supplied value");
        record.Sequence.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_foreign_team_tails_nothing()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamA, userA);

        using (var scope = _fixture.BeginScope())
        {
            var logger = scope.Resolve<IRunRecordLogger>();
            await logger.RunStartedAsync(runId, CancellationToken.None);
            await logger.RunCompletedAsync(runId, TimeSpan.FromSeconds(1), true, CancellationToken.None);
        }

        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var records = await TailAsync(teamB, userB, runId, after: 0);

        records.ShouldBeEmpty("a foreign run yields nothing — the run precheck IS the tenancy boundary");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    private async Task<long> InsertRecordAndCommitAsync(Guid runId, string recordType, string applicationName)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var sequence = await InsertRecordAsync(connection, transaction, runId, recordType);
        await transaction.CommitAsync();
        return sequence;
    }

    private static async Task<long> InsertRecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, string recordType)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO workflow_run_record (id, run_id, record_type, iteration_key, payload_json)
            VALUES (gen_random_uuid(), @run_id, @record_type, '', '{}'::jsonb)
            RETURNING sequence
            """, connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("record_type", recordType);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReadRunCursorAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COALESCE(MAX(sequence), 0) FROM workflow_run_record WHERE run_id = @run_id", connection);
        command.Parameters.AddWithValue("run_id", runId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> WaitForLockAsync(string applicationName, Task<long> insert)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (insert.IsCompleted) return false;
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE application_name = @application_name AND wait_event_type = 'Lock'
                )
                """, connection);
            command.Parameters.AddWithValue("application_name", applicationName);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return true;
            await Task.Delay(20, timeout.Token);
        }

        return false;
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "sse-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<IReadOnlyList<RunRecordView>> TailAsync(Guid teamId, Guid userId, Guid runId, long after)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var streamer = scope.Resolve<IRunRecordStreamer>();

        var records = new List<RunRecordView>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // safety net — a correct tail stops itself at the terminal record
        await foreach (var r in streamer.TailAsync(runId, after, cts.Token))
            records.Add(r);

        return records;
    }
}
