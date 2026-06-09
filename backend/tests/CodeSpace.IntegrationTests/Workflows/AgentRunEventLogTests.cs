using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves the AgentRunEvent append-only log (migration 0040) against real Postgres:
///   1. events append in a DB-assigned, strictly-increasing sequence and reload in emit order,
///      carrying their normalized kind + text + optional JSONB payload;
///   2. kind persists as its STRING name (the closed AgentEventKind vocabulary), not an int;
///   3. the append-only trigger rejects UPDATE and DELETE — a reader at T+1 sees exactly what the
///      agent emitted at T.
/// Schema-only safety net (the AgentRunService that writes these lands in the next slice).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunEventLogTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunEventLogTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Events_append_in_monotonic_sequence_and_reload_in_emit_order()
    {
        var runId = await SeedAgentRunAsync();

        await AppendAsync(runId, AgentEventKind.Queued, "queued", null);
        await AppendAsync(runId, AgentEventKind.CommandExecuted, "npm test", """{"command":"npm test","exitCode":0}""");
        await AppendAsync(runId, AgentEventKind.AssistantMessage, "Fixed the failing tests.", null);
        await AppendAsync(runId, AgentEventKind.Completed, "done", null);

        using var scope = _fixture.BeginScope();
        var events = await scope.Resolve<CodeSpaceDbContext>().AgentRunEvent
            .Where(e => e.AgentRunId == runId).OrderBy(e => e.Sequence).ToListAsync();

        events.Select(e => e.Kind).ShouldBe(new[]
        {
            AgentEventKind.Queued, AgentEventKind.CommandExecuted, AgentEventKind.AssistantMessage, AgentEventKind.Completed,
        });
        events[0].Text.ShouldBe("queued");

        events[1].DataJson.ShouldNotBeNull();
        events[1].DataJson!.ShouldContain("npm test");
        events[0].DataJson.ShouldBeNull();

        events[0].Sequence.ShouldBeGreaterThan(0);
        events[1].Sequence.ShouldBeGreaterThan(events[0].Sequence);
        events[2].Sequence.ShouldBeGreaterThan(events[1].Sequence);
        events[3].Sequence.ShouldBeGreaterThan(events[2].Sequence);

        events.ShouldAllBe(e => e.OccurredAt > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Kind_persists_as_its_string_name_not_an_int()
    {
        var runId = await SeedAgentRunAsync();

        await AppendAsync(runId, AgentEventKind.ApprovalRequested, "approve the patch?", null);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT kind FROM agent_run_event WHERE agent_run_id = @id", conn);
        cmd.Parameters.AddWithValue("@id", runId);

        var stored = (string)(await cmd.ExecuteScalarAsync())!;

        stored.ShouldBe("ApprovalRequested");
    }

    [Fact]
    public async Task Log_is_append_only_update_and_delete_are_rejected()
    {
        var runId = await SeedAgentRunAsync();
        await AppendAsync(runId, AgentEventKind.AssistantMessage, "immutable", null);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var existing = await db.AgentRunEvent.FirstAsync(e => e.AgentRunId == runId);
            existing.Text = "tampered";
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var existing = await db.AgentRunEvent.FirstAsync(e => e.AgentRunId == runId);
            db.AgentRunEvent.Remove(existing);
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        using (var scope = _fixture.BeginScope())
        {
            var survivor = await scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.SingleAsync(e => e.AgentRunId == runId);
            survivor.Text.ShouldBe("immutable");
        }
    }

    private async Task AppendAsync(Guid runId, AgentEventKind kind, string text, string? dataJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = runId, Kind = kind, Text = text, DataJson = dataJson });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAgentRunAsync()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running });
        await db.SaveChangesAsync();

        return runId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
