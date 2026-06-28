using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration: migration 0090's HISTORICAL-ROW backfill — the half the forward-write path
/// (<c>RealHarnessExecutionTests</c>) doesn't cover. A pre-0090 completed run carried its session id only inside
/// <c>result_jsonb</c> (the column didn't exist); the backfill promotes it. Mirrors the project's own backfill-test
/// precedent (<c>TeamRunsIndexFlowTests.The_0082_backfill…</c>): seed pre-migration rows, run the migration body
/// VERBATIM, assert the promotion.
///
/// <para>The load-bearing reason this exists: the backfill reads <c>result_jsonb -&gt;&gt; 'sessionId'</c>, a key that
/// must byte-match what <see cref="AgentJson.Options"/> serializes. So the <c>result_jsonb</c> here is produced by the
/// REAL serializer — change the casing and the backfill silently null-fills every historical row, and THIS test goes
/// red instead.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunSessionIdBackfillTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunSessionIdBackfillTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_0090_backfill_promotes_the_serialized_session_id_and_leaves_the_others_untouched()
    {
        var teamId = await SeedTeamAsync();

        // A pre-0090 COMPLETED row: result_jsonb produced by the REAL AgentJson serializer (so the key is exactly
        // what production emits) with a NULL session_id column — the state the column-add left every historical row in.
        var withId = await SeedPreMigrationRunAsync(teamId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", SessionId = "sess-historical-7a" });

        // A completed row whose run carried NO session id (a pre-session CLI) — must stay null, never fabricated.
        var withoutId = await SeedPreMigrationRunAsync(teamId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });

        // A row already carrying a promoted column value — idempotency: the backfill's `WHERE session_id IS NULL` skips it.
        var alreadyPromoted = await SeedPreMigrationRunAsync(teamId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", SessionId = "sess-in-result" });
        await SetSessionIdColumnAsync(alreadyPromoted, "sess-already-on-column");

        await RunBackfillAsync();

        (await ReadSessionIdAsync(withId)).ShouldBe("sess-historical-7a", "the backfill promoted the camelCase sessionId the real serializer wrote into result_jsonb");
        (await ReadSessionIdAsync(withoutId)).ShouldBeNull("a row whose result carried no session id stays null — never a fabricated value");
        (await ReadSessionIdAsync(alreadyPromoted)).ShouldBe("sess-already-on-column", "idempotent: a row already carrying a column value is left untouched (WHERE session_id IS NULL)");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The migration 0090 backfill body, VERBATIM (0090_agent_run_session_id.sql is immutable once journaled).</summary>
    private async Task RunBackfillAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(@"
UPDATE agent_run
SET session_id = result_jsonb ->> 'sessionId'
WHERE session_id IS NULL
  AND result_jsonb ->> 'sessionId' IS NOT NULL;");
    }

    /// <summary>Create a valid agent_run via the service, then set its result_jsonb to the REAL-serialized result + NULL the session_id column — the exact state a pre-0090 completed row was left in.</summary>
    private async Task<Guid> SeedPreMigrationRunAsync(Guid teamId, AgentRunResult result)
    {
        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(
                new AgentTask { Goal = "historical run", Harness = "codex-cli", Model = null, TimeoutSeconds = 600 },
                teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
            runId = run.Id;
        }

        var resultJson = JsonSerializer.Serialize(result, AgentJson.Options);
        using var write = _fixture.BeginScope();
        await write.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET result_jsonb = {resultJson}::jsonb, session_id = NULL WHERE id = {runId}");

        return runId;
    }

    private async Task SetSessionIdColumnAsync(Guid runId, string sessionId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET session_id = {sessionId} WHERE id = {runId}");
    }

    private async Task<string?> ReadSessionIdAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == runId)).SessionId;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"backfill-{userId:N}@test.local", Name = $"backfill-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"backfill-{teamId:N}", Name = "Backfill Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
