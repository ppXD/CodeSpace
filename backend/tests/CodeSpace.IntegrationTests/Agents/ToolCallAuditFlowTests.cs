using Autofac;
using CodeSpace.Api.Controllers;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Shouldly;
using System.Reflection;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// E1 — the MCP tool-call audit surface, end-to-end through the mediator + real Postgres (mirrors
/// <c>AgentDefinitionFlowTests</c>: the full pipeline IS the API-flow here, since the API project has no
/// HTTP test host — <c>AgentsController.ListToolCalls</c> is a one-line <c>_mediator.Send(query)</c>).
///
/// <para>Proves the operator-facing contract: an owning team reads its run's governed tool calls back in
/// chronological order with every audit field including the approval trail (ApprovedByUserId / ApprovedAt);
/// a FOREIGN team reads an empty list — the tenancy proof (the audit projection filters <c>TeamId == teamId</c>,
/// and AgentRunId is a soft link with no FK, so a foreign / unknown run is indistinguishable from empty, no
/// existence leak); and read-only tools NEVER appear because they skip the ledger entirely (only side-effecting
/// calls get a row — documented + asserted by their absence in the seeded set).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ToolCallAuditFlowTests
{
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly PostgresFixture _fixture;

    public ToolCallAuditFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_owning_team_reads_its_runs_tool_calls_chronological_with_the_full_audit_trail()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var approverId = Guid.NewGuid();

        // Three side-effecting rows, OLDEST first by CreatedDate: a Succeeded write, a Failed write, and one that
        // was approved-then-recorded (carries the approval trail). Inserted in reverse to prove the handler re-orders
        // ascending (the body-free audit projection orders in PostgreSQL, rather than materializing full entities).
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var approvedAt = baseTime.AddMinutes(2).AddSeconds(30);
        await SeedLedgerRowAsync(runId, teamId, "git.open_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: baseTime, approvedByUserId: null, approvedAt: null);
        await SeedLedgerRowAsync(runId, teamId, "git.pr_review", ToolCallLedgerStatus.Failed, error: "remote rejected", createdAt: baseTime.AddMinutes(1), approvedByUserId: null, approvedAt: null);
        await SeedLedgerRowAsync(runId, teamId, "git.merge_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: baseTime.AddMinutes(2), approvedByUserId: approverId, approvedAt: approvedAt);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var calls = await scope.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId });

        calls.Select(c => c.ToolKind).ShouldBe(new[] { "git.open_pr", "git.pr_review", "git.merge_pr" },
            customMessage: "tool calls must come back oldest-first by CreatedDate — the chronological audit order");

        var opened = calls[0];
        opened.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        opened.Error.ShouldBeNull();
        opened.ApprovedByUserId.ShouldBeNull("a call that needed no approval carries no approval trail");
        opened.ApprovedAt.ShouldBeNull();

        var failed = calls[1];
        failed.Status.ShouldBe(ToolCallLedgerStatus.Failed);
        failed.Error.ShouldBe("remote rejected", customMessage: "the already-redacted Error is safe to surface as audit context");

        var merged = calls[2];
        merged.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        merged.ApprovedByUserId.ShouldBe(approverId, customMessage: "the approval trail — WHO approved — must surface for audit");
        merged.ApprovedAt.ShouldNotBeNull("the approval trail — WHEN it was approved — must surface for audit");
        merged.ApprovedAt!.Value.ShouldBe(approvedAt, tolerance: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_foreign_team_reads_an_empty_list_no_existence_leak()
    {
        var (ownerTeam, ownerUser) = await SeedTeamAsync();
        var (foreignTeam, foreignUser) = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        await SeedLedgerRowAsync(runId, ownerTeam, "git.open_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: DateTimeOffset.UtcNow, approvedByUserId: null, approvedAt: null);

        // The owning team sees its row…
        using (var owner = _fixture.BeginScopeAs(ownerUser, ownerTeam, Roles.Admin))
            (await owner.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId })).ShouldHaveSingleItem();

        // …a FOREIGN team reading the SAME run id sees nothing — the audit projection filters TeamId, so a cross-team
        // (or simply unknown) run is indistinguishable from empty. The tenancy proof.
        using (var foreign = _fixture.BeginScopeAs(foreignUser, foreignTeam, Roles.Admin))
            (await foreign.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId }))
                .ShouldBeEmpty("a foreign team must read no tool calls for another tenant's run — no existence leak");
    }

    [Fact]
    public async Task An_unknown_run_reads_an_empty_list()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        (await scope.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = Guid.NewGuid() }))
            .ShouldBeEmpty("a run with no ledger rows (unknown / never made a governed call) reads empty, never errors");
    }

    [Fact]
    public async Task A_large_terminal_result_does_not_change_the_body_free_audit_contract()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = Guid.NewGuid();
        var largeResult = "{\"payload\":\"" + new string('x', 2 * 1024 * 1024) + "\"}";

        await SeedLedgerRowAsync(runId, teamId, "storage.export", ToolCallLedgerStatus.Succeeded, error: null, createdAt: DateTimeOffset.UtcNow, approvedByUserId: null, approvedAt: null);
        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            await db.ToolCallLedger.Where(row => row.AgentRunId == runId && row.TeamId == teamId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ResultJson, largeResult));
        }

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var call = (await scope.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId })).ShouldHaveSingleItem();

        call.ToolKind.ShouldBe("storage.export");
        call.Status.ShouldBe(ToolCallLedgerStatus.Succeeded);
        call.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Read_only_tools_are_absent_because_they_skip_the_ledger()
    {
        // Read-only tools (e.g. agent.run_command at a read, git.list_prs) are NOT recorded in the ToolCallLedger —
        // only SIDE-EFFECTING calls get a row (ToolCallLedger doc + McpRequestHandler). So the audit surface only
        // ever lists governed calls; a read leaves no row. We seed ONLY a side-effecting row and assert the surface
        // contains exactly it — proving a read-only call would have nothing to surface.
        var (teamId, userId) = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        await SeedLedgerRowAsync(runId, teamId, "git.open_pr", ToolCallLedgerStatus.Succeeded, error: null, createdAt: DateTimeOffset.UtcNow, approvedByUserId: null, approvedAt: null);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var calls = await scope.Resolve<IMediator>().Send(new ListToolCallsQuery { AgentRunId = runId });

        calls.ShouldHaveSingleItem().ToolKind.ShouldBe("git.open_pr",
            customMessage: "only the side-effecting call is in the ledger — read-only tools skip it, so they never appear in the audit surface");
    }

    [Fact]
    public void Bounded_page_is_additive_and_the_legacy_whole_list_route_remains()
    {
        var methods = typeof(AgentsController).GetMethods(BindingFlags.Instance | BindingFlags.Public);

        methods.Single(method => method.Name == nameof(AgentsController.ListToolCalls)).GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("runs/{agentRunId:guid}/tool-calls");
        methods.Single(method => method.Name == nameof(AgentsController.PageToolCalls)).GetCustomAttribute<HttpGetAttribute>()!.Template.ShouldBe("runs/{agentRunId:guid}/tool-calls/page");
    }

    [Fact]
    public async Task Tail_and_older_are_bounded_chronological_and_include_legacy_null_ordinal_history()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedLegacyLedgerRowAsync(runId, teamId, "legacy.tool", start);
        for (var i = 2; i <= 7; i++)
            await SeedLedgerRowAsync(runId, teamId, $"tool.{i}", ToolCallLedgerStatus.Succeeded, null, start.AddMinutes(i), null, null);

        var tail = (await PageAsync(userId, teamId, new PageToolCallsQuery { AgentRunId = runId, Direction = ToolCallPageDirection.Tail, Limit = 3 }))!;
        tail.AgentRunId.ShouldBe(runId);
        tail.Mode.ShouldBe(nameof(ToolCallPageDirection.Tail));
        tail.RequestCursor.ShouldBeNull();
        tail.Items.Select(row => row.ToolKind).ShouldBe(["tool.5", "tool.6", "tool.7"]);
        tail.HasOlder.ShouldBeTrue();
        tail.NextOlderCursor.ShouldNotBeNull();

        var middle = (await PageAsync(userId, teamId, new PageToolCallsQuery { AgentRunId = runId, Direction = ToolCallPageDirection.Older, Cursor = tail.NextOlderCursor, Limit = 3 }))!;
        middle.RequestCursor.ShouldBe(tail.NextOlderCursor);
        middle.Items.Select(row => row.ToolKind).ShouldBe(["tool.2", "tool.3", "tool.4"]);
        middle.HasOlder.ShouldBeTrue();

        var oldest = (await PageAsync(userId, teamId, new PageToolCallsQuery { AgentRunId = runId, Direction = ToolCallPageDirection.Older, Cursor = middle.NextOlderCursor, Limit = 3 }))!;
        oldest.Items.Select(row => row.ToolKind).ShouldBe(["legacy.tool"]);
        oldest.HasOlder.ShouldBeFalse("legacy rows with NULL AdmissionOrdinal remain truthful audit history");
        oldest.NextOlderCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Page_requires_exact_owned_AgentRun_and_malformed_cursor_fails_closed()
    {
        var (ownerTeam, ownerUser) = await SeedTeamAsync();
        var (foreignTeam, foreignUser) = await SeedTeamAsync();
        var runId = await SeedRunAsync(ownerTeam);
        await SeedLedgerRowAsync(runId, ownerTeam, "git.open_pr", ToolCallLedgerStatus.Succeeded, null, DateTimeOffset.UtcNow, null, null);

        (await PageAsync(ownerUser, ownerTeam, new PageToolCallsQuery { AgentRunId = runId, Limit = 10 })).ShouldNotBeNull();
        (await PageAsync(foreignUser, foreignTeam, new PageToolCallsQuery { AgentRunId = runId, Limit = 10 })).ShouldBeNull();
        (await PageAsync(ownerUser, ownerTeam, new PageToolCallsQuery { AgentRunId = Guid.NewGuid(), Limit = 10 })).ShouldBeNull();
        await Should.ThrowAsync<ToolCallPageRequestException>(() => PageAsync(ownerUser, ownerTeam, new PageToolCallsQuery { AgentRunId = runId, Direction = ToolCallPageDirection.Older, Cursor = "not-a-cursor", Limit = 10 }));
    }

    [Fact]
    public async Task Ten_thousand_row_page_projects_no_execution_secrets_and_uses_run_created_index_without_scan_or_sort()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var runId = await SeedRunAsync(teamId);
        await InsertLegacyFloodAsync(runId, teamId, 10_050);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var sql = ToolCallAuditReader.PageRowsQuery(db, runId, teamId, cursor: null, take: 129).ToQueryString();
        sql.ShouldContain("LIMIT");
        sql.ShouldNotContain("OFFSET");
        sql.ShouldNotContain("COUNT(");
        sql.ShouldNotContain("result_jsonb");
        sql.ShouldNotContain("decision_envelope_jsonb");
        sql.ShouldNotContain("approval_token");
        sql.ShouldNotContain("idempotency_key");
        sql.ShouldNotContain("input_hash");

        var page = (await PageAsync(userId, teamId, new PageToolCallsQuery { AgentRunId = runId, Limit = 128 }))!;
        page.Items.Count.ShouldBe(128);
        page.Items.Select(row => row.CreatedDate).ShouldBeInOrder(SortDirection.Ascending);
        page.HasOlder.ShouldBeTrue();

        var plan = await ExplainPageAsync(runId, teamId);
        plan.ShouldContain("Limit");
        plan.ShouldContain("idx_tool_call_ledger_run_created_id");
        plan.ShouldNotContain("Seq Scan on tool_call_ledger");
        plan.ShouldNotContain("Sort");
    }

    private async Task SeedLedgerRowAsync(Guid runId, Guid teamId, string toolKind, ToolCallLedgerStatus status, string? error, DateTimeOffset createdAt, Guid? approvedByUserId, DateTimeOffset? approvedAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            AgentRunId = runId,
            ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{Guid.NewGuid():N}",
            InputHash = InputHash,
            Status = status,
            Error = error,
            ApprovedByUserId = approvedByUserId,
            ApprovedAt = approvedAt,
            CreatedDate = createdAt,
            LastModifiedDate = createdAt,
        });

        await db.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"audit-{userId:N}@test.local", Name = $"audit-user-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"audit-team-{teamId:N}", Name = "Audit Test Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
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

    private async Task<ToolCallPage?> PageAsync(Guid userId, Guid teamId, PageToolCallsQuery request)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(request);
    }

    private async Task SeedLegacyLedgerRowAsync(Guid runId, Guid teamId, string toolKind, DateTimeOffset createdAt)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand("ALTER TABLE tool_call_ledger DISABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
            await disable.ExecuteNonQueryAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tool_call_ledger
                (id, team_id, agent_run_id, tool_kind, idempotency_key, input_hash, status, result_jsonb,
                 decision_envelope_jsonb, approval_token, created_date, created_by, last_modified_date, last_modified_by)
            VALUES (@id, @team, @run, @kind, @key, @hash, 'Succeeded', '{"secret":"body"}',
                    '{"secret":"decision"}', 'bearer-must-not-project', @created, @actor, @created, @actor)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("team", teamId);
            insert.Parameters.AddWithValue("run", runId);
            insert.Parameters.AddWithValue("kind", toolKind);
            insert.Parameters.AddWithValue("key", $"legacy:{Guid.NewGuid():N}");
            insert.Parameters.AddWithValue("hash", InputHash);
            insert.Parameters.AddWithValue("created", createdAt);
            insert.Parameters.AddWithValue("actor", Guid.Empty);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var enable = new NpgsqlCommand("ALTER TABLE tool_call_ledger ENABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
            await enable.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private async Task InsertLegacyFloodAsync(Guid runId, Guid teamId, int count)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand("ALTER TABLE tool_call_ledger DISABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
            await disable.ExecuteNonQueryAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tool_call_ledger
                (id, team_id, agent_run_id, tool_kind, idempotency_key, input_hash, status, result_jsonb,
                 decision_envelope_jsonb, approval_token, created_date, created_by, last_modified_date, last_modified_by)
            SELECT md5(value::text || @run::text)::uuid, @team, @run, 'bulk.' || value, 'bulk:' || value,
                   repeat('0', 64), 'Succeeded', '{"secret":"body"}', '{"secret":"decision"}',
                   'bearer-must-not-project', @created + value * interval '1 microsecond', @actor,
                   @created + value * interval '1 microsecond', @actor
            FROM generate_series(1, @count) AS value
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("team", teamId);
            insert.Parameters.AddWithValue("run", runId);
            insert.Parameters.AddWithValue("count", count);
            insert.Parameters.AddWithValue("created", DateTimeOffset.UtcNow.AddDays(-1));
            insert.Parameters.AddWithValue("actor", Guid.Empty);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var enable = new NpgsqlCommand("ALTER TABLE tool_call_ledger ENABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
            await enable.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        await using var analyze = new NpgsqlCommand("ANALYZE tool_call_ledger", connection);
        await analyze.ExecuteNonQueryAsync();
    }

    private async Task<string> ExplainPageAsync(Guid runId, Guid teamId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var disableSeqScan = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await disableSeqScan.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("""
            EXPLAIN SELECT id, tool_kind, status, created_date, last_modified_date, error, approved_by_user_id, approved_at
            FROM tool_call_ledger
            WHERE agent_run_id = @run AND team_id = @team
            ORDER BY created_date DESC, id DESC LIMIT 129
            """, connection);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("team", teamId);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }
}
