using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves the AgentRun lifecycle record (migration 0039) round-trips through EF against real Postgres:
///   1. full lifecycle Queued → Running → Succeeded, with task/result JSONB + nullable links;
///   2. status persists as its STRING name (not an int) — guards the HasConversion regressing;
///   3. the xmin concurrency token rejects a stale update — the no-double-transition guarantee.
/// Schema-only safety net (no service yet — that's the next slice).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunRoundTripTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunRoundTripTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Agent_run_round_trips_through_its_full_lifecycle()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            scope.Resolve<CodeSpaceDbContext>().AgentRun.Add(new AgentRun
            {
                Id = runId,
                TeamId = teamId,
                Harness = "codex-cli",
                Status = AgentRunStatus.Queued,
                TaskJson = """{"goal":"Fix the failing billing tests","harness":"codex-cli","model":"gpt-5.3-codex"}""",
            });
            await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.SingleAsync(r => r.Id == runId);

            run.Status.ShouldBe(AgentRunStatus.Queued);
            run.Harness.ShouldBe("codex-cli");
            run.TaskJson.ShouldContain("billing");
            run.WorkflowRunId.ShouldBeNull();
            run.NodeId.ShouldBeNull();
            run.ResultJson.ShouldBeNull();
            run.StartedAt.ShouldBeNull();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var run = await db.AgentRun.SingleAsync(r => r.Id == runId);
            run.Status = AgentRunStatus.Running;
            run.StartedAt = DateTimeOffset.UtcNow;
            run.HeartbeatAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var run = await db.AgentRun.SingleAsync(r => r.Id == runId);
            run.Status.ShouldBe(AgentRunStatus.Running);
            run.Status = AgentRunStatus.Succeeded;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.ResultJson = """{"status":"Succeeded","exitReason":"completed","summary":"done","changedFiles":["src/a.ts"]}""";
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.SingleAsync(r => r.Id == runId);

            run.Status.ShouldBe(AgentRunStatus.Succeeded);
            run.ResultJson.ShouldNotBeNull();
            run.ResultJson!.ShouldContain("completed");
            run.StartedAt.ShouldNotBeNull();
            run.CompletedAt.ShouldNotBeNull();
            run.HeartbeatAt.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Status_persists_as_its_string_name_not_an_int()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.TimedOut });
            await db.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM agent_run WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", runId);

        var stored = (string)(await cmd.ExecuteScalarAsync())!;

        stored.ShouldBe("TimedOut");
    }

    [Fact]
    public async Task Concurrent_update_to_the_same_run_is_rejected_by_the_xmin_token()
    {
        var teamId = await SeedTeamAsync();
        var runId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Queued });
            await db.SaveChangesAsync();
        }

        using var scopeA = _fixture.BeginScope();
        using var scopeB = _fixture.BeginScope();
        var dbA = scopeA.Resolve<CodeSpaceDbContext>();
        var dbB = scopeB.Resolve<CodeSpaceDbContext>();

        var runA = await dbA.AgentRun.SingleAsync(r => r.Id == runId);
        var runB = await dbB.AgentRun.SingleAsync(r => r.Id == runId);   // both loaded the same xmin

        runA.Status = AgentRunStatus.Running;
        await dbA.SaveChangesAsync();   // first writer wins; xmin bumps

        runB.Status = AgentRunStatus.Cancelled;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());   // stale xmin → rejected
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
