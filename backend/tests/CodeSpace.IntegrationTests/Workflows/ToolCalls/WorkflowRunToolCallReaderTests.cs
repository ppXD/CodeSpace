using System.Data;
using System.Reflection;
using Autofac;
using CodeSpace.Api.Controllers;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.ToolCalls;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Dtos.Workflows.ToolCalls;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.ToolCalls;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunToolCallReaderTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunToolCallReaderTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Page_is_created_id_keyset_bounded_and_never_mixes_run_team_or_call_ordinal()
    {
        var owner = await SeedWorldAsync();
        var foreign = await SeedWorldAsync();
        var emptyRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, owner.WorkflowId, owner.TeamId);
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var ids = new[]
        {
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
        };
        await SeedCallAsync(owner, ids[0], at, callOrdinal: 900);
        await SeedCallAsync(owner, ids[1], at, callOrdinal: 1);
        await SeedCallAsync(owner, ids[2], at, callOrdinal: 7);
        await SeedCallAsync(foreign, Guid.Parse("f0000000-0000-0000-0000-000000000004"), at, callOrdinal: 999);

        var first = await PageAsync(owner.TeamId, owner.RunId, null, 2);
        first.ShouldNotBeNull();
        first.Items.Select(value => value.ToolCallId).ShouldBe([ids[2], ids[1]]);
        first.Items.Select(value => value.CallOrdinal).ShouldBe([7L, 1L], "CallOrdinal is per Agent Run and never the Workflow Run page order");
        first.RequestCursor.ShouldBeNull();
        first.Limit.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();

        var second = await PageAsync(owner.TeamId, owner.RunId, first.NextCursor, 2);
        second.ShouldNotBeNull();
        second.RequestCursor.ShouldBe(first.NextCursor);
        second.Limit.ShouldBe(2);
        second.Items.Select(value => value.ToolCallId).ShouldBe([ids[0]]);
        second.NextCursor.ShouldBeNull();
        first.Items.Concat(second.Items).Select(value => value.ToolCallId).Distinct().Count().ShouldBe(3);

        var empty = await PageAsync(owner.TeamId, emptyRunId, null, 20);
        empty.ShouldNotBeNull();
        empty.Items.ShouldBeEmpty();
        (await PageAsync(foreign.TeamId, owner.RunId, null, 20)).ShouldBeNull();
        (await PageAsync(owner.TeamId, Guid.NewGuid(), null, 20)).ShouldBeNull();
    }

    [Fact]
    public async Task Stable_detail_is_scope_conflated_and_attempts_are_one_bounded_ordered_query()
    {
        var owner = await SeedWorldAsync();
        var foreign = await SeedWorldAsync();
        var call = await SeedCallAsync(owner, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-5), callOrdinal: 3);
        await SeedAttemptsAsync(owner, call.Id, WorkflowRunToolCallReader.MaximumAttempts + 1);

        var detail = await DetailAsync(owner.TeamId, owner.RunId, call.Id);
        detail.ShouldNotBeNull();
        detail.Call.ToolCallId.ShouldBe(call.Id);
        detail.Call.ToolAdapterKind.ShouldBe("governed-tool-call/v1");
        detail.Call.ToolName.ShouldBe("git.open_pr");
        detail.Call.EffectClass.ShouldBe(WorkflowRunToolCallEffectClass.SideEffecting);
        detail.Attempts.Count.ShouldBe(WorkflowRunToolCallReader.MaximumAttempts);
        detail.Attempts.Select(value => value.AttemptOrdinal).ShouldBe(Enumerable.Range(1, WorkflowRunToolCallReader.MaximumAttempts));
        detail.Attempts.ShouldAllBe(value => value.Status == WorkflowRunToolCallAttemptObservationStatus.Succeeded);
        detail.AttemptsTruncated.ShouldBeTrue();

        (await DetailAsync(foreign.TeamId, owner.RunId, call.Id)).ShouldBeNull();
        (await DetailAsync(owner.TeamId, foreign.RunId, call.Id)).ShouldBeNull();
        (await DetailAsync(owner.TeamId, owner.RunId, Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task Ten_thousand_row_page_plan_uses_run_created_backward_keyset_without_seqscan_or_sort()
    {
        var world = await SeedWorldAsync();
        await SeedCallFloodAsync(world, 10_050);

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + WorkflowRunToolCallReader.PageSql, connection, transaction);
        command.Parameters.AddWithValue("team_id", world.TeamId);
        command.Parameters.AddWithValue("run_id", world.RunId);
        command.Parameters.AddWithValue("has_cursor", true);
        command.Parameters.Add(new NpgsqlParameter("cursor_created_at", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow.AddDays(1) });
        command.Parameters.AddWithValue("cursor_id", Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        command.Parameters.AddWithValue("take", 101);
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
        var text = string.Join('\n', plan);

        text.ShouldContain("ix_workflow_run_tool_call_run_created");
        text.ShouldNotContain("Seq Scan on workflow_run_tool_call");
        text.ShouldNotContain("Sort");
        await transaction.RollbackAsync();
    }

    [Fact]
    public void Api_routes_are_run_scoped_and_do_not_claim_an_all_tools_feed()
    {
        var list = typeof(WorkflowRunsController).GetMethod(nameof(WorkflowRunsController.ListToolCalls))!;
        var detail = typeof(WorkflowRunsController).GetMethod(nameof(WorkflowRunsController.GetToolCall))!;
        list.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("{runId:guid}/tool-calls");
        detail.GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("{runId:guid}/tool-calls/{toolCallId:guid}");
    }

    private async Task<WorkflowRunToolCallPage?> PageAsync(Guid teamId, Guid runId, string? cursor, int limit)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowRunToolCallReader>()
            .ReadPageAsync(new WorkflowRunToolCallPageRequest(teamId, runId, cursor, limit), CancellationToken.None);
    }

    private async Task<WorkflowRunToolCallDetail?> DetailAsync(Guid teamId, Guid runId, Guid callId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowRunToolCallReader>()
            .ReadDetailAsync(new WorkflowRunToolCallDetailRequest(teamId, runId, callId), CancellationToken.None);
    }

    private async Task<World> SeedWorldAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "tool-call-reader-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = new List<WorkflowActivationInput>(), Enabled = true,
            });
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        return new World(teamId, workflowId, runId);
    }

    private async Task<WorkflowRunToolCall> SeedCallAsync(World world, Guid id, DateTimeOffset createdAt, long callOrdinal)
    {
        using var scope = _fixture.BeginScope();
        var call = new WorkflowRunToolCall
        {
            Id = id, TeamId = world.TeamId, WorkflowRunId = world.RunId, IterationKey = string.Empty,
            CallOrdinal = callOrdinal, Purpose = "agent.governed-side-effect/v1", ToolKind = "governed-tool-call/v1",
            ToolName = "git.open_pr", EffectClass = ToolCallEffectClass.SideEffecting,
            ArgumentsRedaction = NativeRecordRedaction.Withheld, SourceKind = "tool-call-ledger/v1",
            SourceCorrelationId = Guid.NewGuid(), CaptureSource = "tool-call-ledger/v1",
            CaptureCompleteness = WorkflowRunCaptureCompleteness.Unavailable, State = ToolCallState.Pending,
            AttemptCount = 0, NextAttemptOrdinal = 1, Revision = 1, SchemaVersion = WorkflowRunDataContract.CurrentVersion,
            CreatedAt = createdAt, LastModifiedAt = createdAt,
        };
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunToolCall.Add(call);
        await db.SaveChangesAsync();
        return call;
    }

    private async Task SeedAttemptsAsync(World world, Guid callId, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        for (var ordinal = 1; ordinal <= count; ordinal++)
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-4).AddTicks(ordinal);
            var attemptId = Guid.NewGuid();
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO workflow_run_tool_call_attempt
                    (id, team_id, workflow_run_id, tool_call_id, attempt_ordinal, status, result_redaction,
                     capture_source, capture_completeness, started_at, revision, schema_version, created_at, last_modified_at)
                VALUES (@id, @team, @run, @call, @ordinal, 'Running', 'Withheld',
                        'tool-call-ledger/v1', 'Unavailable', @at, 1, 1, @at, @at)
                """, connection))
            {
                insert.Parameters.AddWithValue("id", attemptId);
                insert.Parameters.AddWithValue("team", world.TeamId);
                insert.Parameters.AddWithValue("run", world.RunId);
                insert.Parameters.AddWithValue("call", callId);
                insert.Parameters.AddWithValue("ordinal", ordinal);
                insert.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
                await insert.ExecuteNonQueryAsync();
            }
            await using var terminal = new NpgsqlCommand("""
                UPDATE workflow_run_tool_call_attempt
                SET status = 'Succeeded', completed_at = @at, last_modified_at = @at, revision = 2
                WHERE id = @id
                """, connection);
            terminal.Parameters.AddWithValue("id", attemptId);
            terminal.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            await terminal.ExecuteNonQueryAsync();
        }
    }

    private async Task SeedCallFloodAsync(World world, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO workflow_run_tool_call
                (id, team_id, workflow_run_id, iteration_key, call_ordinal, purpose, tool_kind, tool_name,
                 effect_class, arguments_redaction, capture_source, capture_completeness, state,
                 attempt_count, next_attempt_ordinal, revision, schema_version, created_at, last_modified_at)
            SELECT md5(@salt || value::text)::uuid, @team, @run, '', value,
                   'agent.governed-side-effect/v1', 'governed-tool-call/v1', 'git.open_pr',
                   'SideEffecting', 'Withheld', 'tool-call-ledger/v1', 'Unavailable', 'Pending',
                   0, 1, 1, 1, @at + value * interval '1 microsecond', @at + value * interval '1 microsecond'
            FROM generate_series(1, @count) AS value
            """, connection);
        insert.Parameters.AddWithValue("salt", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("team", world.TeamId);
        insert.Parameters.AddWithValue("run", world.RunId);
        insert.Parameters.AddWithValue("count", count);
        insert.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = DateTimeOffset.UtcNow.AddDays(-1) });
        await insert.ExecuteNonQueryAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE workflow_run_tool_call", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private sealed record World(Guid TeamId, Guid WorkflowId, Guid RunId);
}
