using System.Reflection;
using Autofac;
using CodeSpace.Api.Controllers;
using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// Paging contract for the append-only Agent Run event plane. The terminal consumes this bounded API while the legacy
/// whole-log endpoint remains byte-for-byte available to compact preview callers.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunEventPageFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunEventPageFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void The_additive_page_endpoint_is_a_distinct_route_and_the_legacy_endpoint_still_exists()
    {
        var methods = typeof(AgentsController).GetMethods(BindingFlags.Instance | BindingFlags.Public);

        Route(methods, nameof(AgentsController.ListRunEvents)).ShouldBe("runs/{agentRunId:guid}/events");
        Route(methods, nameof(AgentsController.PageRunEvents)).ShouldBe("runs/{agentRunId:guid}/events/page");
    }

    [Fact]
    public async Task An_overflow_cursor_fails_closed_before_any_event_read()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        var error = await Should.ThrowAsync<AgentRunEventPageRequestException>(() => scope.Resolve<IMediator>().Send(new PageAgentRunEventsQuery
        {
            AgentRunId = Guid.NewGuid(), Direction = AgentRunEventPageDirection.Newer, Cursor = "9223372036854775808",
        }));

        error.Errors.ShouldContain(message => message.Contains("non-negative invariant decimal cursor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tail_older_and_newer_are_bounded_ascending_and_preserve_inline_and_offloaded_data()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var artifactId = Guid.NewGuid();
        var all = new List<AgentRunEvent>();
        for (var i = 1; i <= 7; i++)
            all.Add(await AppendAsync(runId, $"event-{i}", i == 6 ? """{"inline":true}""" : null, i == 7 ? artifactId : null));

        var tail = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Tail, null, 3))!;
        tail.AgentRunId.ShouldBe(runId);
        tail.Mode.ShouldBe(nameof(AgentRunEventPageDirection.Tail));
        tail.RequestCursor.ShouldBeNull();
        tail.Items.Select(item => item.Sequence).ShouldBe(all.Skip(4).Select(item => item.Sequence));
        tail.Items.Select(item => item.Sequence).ShouldBeInOrder(SortDirection.Ascending);
        tail.Items[1].Data.ShouldBe("""{"inline": true}""", "JSONB's existing string semantics must survive the projection");
        tail.Items[2].Data.ShouldBeNull();
        tail.Items[2].DataArtifactId.ShouldBe(artifactId);
        tail.HasOlder.ShouldBeTrue();
        tail.HasNewer.ShouldBeFalse();
        tail.NextOlderCursor.ShouldBe(all[4].Sequence.ToString());
        tail.NextNewerCursor.ShouldBe(all[6].Sequence.ToString());

        var older = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Older, tail.NextOlderCursor, 3))!;
        older.AgentRunId.ShouldBe(runId);
        older.Mode.ShouldBe(nameof(AgentRunEventPageDirection.Older));
        older.RequestCursor.ShouldBe(tail.NextOlderCursor);
        older.Items.Select(item => item.Sequence).ShouldBe(all.Skip(1).Take(3).Select(item => item.Sequence));
        older.Items.Select(item => item.Sequence).ShouldBeInOrder(SortDirection.Ascending);
        older.HasOlder.ShouldBeTrue();
        older.HasNewer.ShouldBeTrue();

        var newer = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Newer, older.NextNewerCursor, 3))!;
        newer.AgentRunId.ShouldBe(runId);
        newer.Mode.ShouldBe(nameof(AgentRunEventPageDirection.Newer));
        newer.RequestCursor.ShouldBe(older.NextNewerCursor);
        newer.Items.Select(item => item.Sequence).ShouldBe(all.Skip(4).Select(item => item.Sequence));
        newer.Items.Select(item => item.Sequence).ShouldBeInOrder(SortDirection.Ascending);
        newer.HasOlder.ShouldBeTrue();
        newer.HasNewer.ShouldBeFalse();
    }

    [Fact]
    public async Task Missing_and_cross_team_runs_are_indistinguishable_while_an_owned_empty_run_is_a_real_empty_page()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var ownedEmpty = await SeedRunAsync(teamA);
        var foreign = await SeedRunAsync(teamB);
        await AppendAsync(foreign, "foreign", """{"secret":true}""", null);

        (await ReadAsync(userA, teamA, foreign, AgentRunEventPageDirection.Tail, null, 10)).ShouldBeNull();
        (await ReadAsync(userA, teamA, Guid.NewGuid(), AgentRunEventPageDirection.Tail, null, 10)).ShouldBeNull();

        var empty = (await ReadAsync(userA, teamA, ownedEmpty, AgentRunEventPageDirection.Tail, null, 10))!;
        empty.Items.ShouldBeEmpty();
        empty.HasOlder.ShouldBeFalse();
        empty.HasNewer.ShouldBeFalse();
        empty.NextOlderCursor.ShouldBeNull();
        empty.NextNewerCursor.ShouldBe("0");
    }

    [Fact]
    public async Task A_concurrent_append_after_the_tail_is_returned_once_by_the_newer_cursor()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await AppendAsync(runId, "one", null, null);
        await AppendAsync(runId, "two", null, null);
        var tail = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Tail, null, 10))!;

        var appended = await AppendAsync(runId, "appended-after-tail", """{"live":true}""", null);
        var delta = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Newer, tail.NextNewerCursor, 10))!;

        delta.Items.Select(item => item.Sequence).ShouldBe([appended.Sequence]);
        delta.Items.Single().Text.ShouldBe("appended-after-tail");
        delta.NextNewerCursor.ShouldBe(appended.Sequence.ToString());

        var emptyDelta = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Newer, delta.NextNewerCursor, 10))!;
        emptyDelta.Items.ShouldBeEmpty();
        emptyDelta.NextNewerCursor.ShouldBe(appended.Sequence.ToString(), "an empty live poll must retain its cursor rather than restart at zero");
    }

    [Fact]
    public async Task Exact_open_kind_filter_pages_only_matching_rows_and_computes_edges_from_that_filtered_set()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await AppendAsync(runId, "reason-1", null, null);
        var first = await AppendAsync(runId, "tool-1", """{"name":"Read"}""", null, AgentEventKind.ToolCall);
        await AppendAsync(runId, "reason-2", null, null);
        var second = await AppendAsync(runId, "tool-2", null, Guid.NewGuid(), AgentEventKind.ToolCall);
        await AppendAsync(runId, "reason-3", null, null);
        var third = await AppendAsync(runId, "tool-3", null, null, AgentEventKind.ToolCall);

        var tail = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Tail, null, 2, nameof(AgentEventKind.ToolCall)))!;
        tail.KindFilter.ShouldBe(nameof(AgentEventKind.ToolCall));
        tail.Items.Select(item => item.Sequence).ShouldBe([second.Sequence, third.Sequence]);
        tail.Items.ShouldAllBe(item => item.Kind == AgentEventKind.ToolCall);
        tail.HasOlder.ShouldBeTrue();
        tail.NextOlderCursor.ShouldBe(second.Sequence.ToString());

        var older = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Older, tail.NextOlderCursor, 2, nameof(AgentEventKind.ToolCall)))!;
        older.Items.Select(item => item.Sequence).ShouldBe([first.Sequence]);
        older.HasOlder.ShouldBeFalse();
        older.HasNewer.ShouldBeTrue();

        await AppendAsync(runId, "reason-after", null, null);
        var noMatchingDelta = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Newer, tail.NextNewerCursor, 2, nameof(AgentEventKind.ToolCall)))!;
        noMatchingDelta.Items.ShouldBeEmpty();
        noMatchingDelta.HasNewer.ShouldBeFalse("non-matching rows never create a phantom filtered page");
        noMatchingDelta.NextNewerCursor.ShouldBe(tail.NextNewerCursor);

        var fourth = await AppendAsync(runId, "tool-4", null, null, AgentEventKind.ToolCall);
        var matchingDelta = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Newer, tail.NextNewerCursor, 2, nameof(AgentEventKind.ToolCall)))!;
        matchingDelta.Items.Select(item => item.Sequence).ShouldBe([fourth.Sequence]);

        var future = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Tail, null, 2, "FutureHarnessEvent"))!;
        future.KindFilter.ShouldBe("FutureHarnessEvent");
        future.Items.ShouldBeEmpty("the open discriminator is accepted exactly even when this deployment has no producer for it");
        future.HasOlder.ShouldBeFalse();
    }

    [Fact]
    public async Task Ten_thousand_events_still_compile_to_limit_keyset_queries_served_by_the_run_sequence_index()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await InsertFloodAsync(runId, 10_050);

        using var shapeScope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var db = shapeScope.Resolve<CodeSpaceDbContext>();
        var sql = PageAgentRunEventsQueryHandler.PageRowsQuery(db, runId, AgentRunEventPageDirection.Tail, cursor: 0, take: 201, kindFilter: null).ToQueryString();
        sql.ShouldContain("LIMIT");
        sql.ShouldContain("ORDER BY");
        sql.ShouldNotContain("OFFSET");
        sql.ShouldNotContain("COUNT(");

        var page = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Tail, null, 200))!;
        page.Items.Count.ShouldBe(200);
        page.Items.Select(item => item.Sequence).ShouldBeInOrder(SortDirection.Ascending);
        page.HasOlder.ShouldBeTrue();

        var tailPlan = await ExplainAsync(runId, "ORDER BY sequence DESC LIMIT 201");
        tailPlan.ShouldContain("Limit");
        tailPlan.ShouldContain("idx_are_run_sequence");
        tailPlan.ShouldNotContain("Seq Scan on agent_run_event");
        tailPlan.ShouldNotContain("Sort");

        var newerPlan = await ExplainAsync(runId, "AND sequence > 0 ORDER BY sequence ASC LIMIT 201");
        newerPlan.ShouldContain("Limit");
        newerPlan.ShouldContain("idx_are_run_sequence");
        newerPlan.ShouldNotContain("Seq Scan on agent_run_event");
        newerPlan.ShouldNotContain("Sort");
    }

    [Fact]
    public async Task Filtered_tool_call_pages_use_the_run_kind_sequence_index_without_scan_or_sort()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await InsertMixedFloodAsync(runId, 10_050);

        using var shapeScope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var db = shapeScope.Resolve<CodeSpaceDbContext>();
        var sql = PageAgentRunEventsQueryHandler.PageRowsQuery(db, runId, AgentRunEventPageDirection.Tail, cursor: 0, take: 201, kindFilter: nameof(AgentEventKind.ToolCall)).ToQueryString();
        sql.ShouldContain("kind");
        sql.ShouldContain("ToolCall");
        sql.ShouldContain("LIMIT");
        sql.ShouldNotContain("OFFSET");

        var page = (await ReadAsync(userId, teamId, runId, AgentRunEventPageDirection.Tail, null, 200, nameof(AgentEventKind.ToolCall)))!;
        page.Items.Count.ShouldBe(200);
        page.Items.ShouldAllBe(item => item.Kind == AgentEventKind.ToolCall);

        var plan = await ExplainAsync(runId, "AND kind = 'ToolCall' ORDER BY sequence DESC LIMIT 201");
        plan.ShouldContain("Limit");
        plan.ShouldContain("idx_are_run_kind_sequence");
        plan.ShouldNotContain("Seq Scan on agent_run_event");
        plan.ShouldNotContain("Sort");
    }

    private static string Route(MethodInfo[] methods, string methodName) =>
        methods.Single(method => method.Name == methodName).GetCustomAttribute<HttpGetAttribute>()!.Template!;

    private async Task<AgentRunEventPage?> ReadAsync(Guid userId, Guid teamId, Guid runId, AgentRunEventPageDirection direction, string? cursor, int limit, string? kindFilter = null)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new PageAgentRunEventsQuery
        {
            AgentRunId = runId, Direction = direction, Cursor = cursor, Limit = limit, KindFilter = kindFilter,
        });
    }

    private async Task<Guid> SeedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running };
        db.AgentRun.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<AgentRunEvent> AppendAsync(Guid runId, string text, string? dataJson, Guid? dataArtifactId, AgentEventKind kind = AgentEventKind.Reasoning)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var value = new AgentRunEvent
        {
            Id = Guid.NewGuid(), AgentRunId = runId, Kind = kind, Text = text,
            DataJson = dataJson, DataArtifactId = dataArtifactId, OccurredAt = DateTimeOffset.UtcNow,
        };
        db.AgentRunEvent.Add(value);
        await db.SaveChangesAsync();
        return value;
    }

    private async Task InsertFloodAsync(Guid runId, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO agent_run_event (agent_run_id, kind, text, data_json, occurred_at)
            SELECT @run, 'Reasoning', 'flood-' || value, jsonb_build_object('ordinal', value), NOW()
            FROM generate_series(1, @count) AS value
            """, connection);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE agent_run_event", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private async Task InsertMixedFloodAsync(Guid runId, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO agent_run_event (agent_run_id, kind, text, data_json, occurred_at)
            SELECT @run, CASE WHEN value % 3 = 0 THEN 'ToolCall' ELSE 'Reasoning' END,
                   'mixed-' || value, jsonb_build_object('ordinal', value), NOW()
            FROM generate_series(1, @count) AS value
            """, connection);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE agent_run_event", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private async Task<string> ExplainAsync(Guid runId, string suffix)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var disableSeqScan = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await disableSeqScan.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand($"EXPLAIN SELECT sequence, kind, text, data_json, data_artifact_id, occurred_at FROM agent_run_event WHERE agent_run_id = @run {suffix}", connection);
        command.Parameters.AddWithValue("run", runId);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }
}
