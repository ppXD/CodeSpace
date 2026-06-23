using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Context.Sources;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The <c>get_context</c> retrieval tool against real Postgres — both the SOURCE behaviours (full-not-clipped prior
/// turns, query filter, char-budget, tenancy, null-turn exclusion; the rolling summary) AND the ASSEMBLED tool path
/// through the real <see cref="McpRequestHandler"/> + real DI registry: the handler stamps the run id onto the call,
/// the tool resolves <c>AgentRun → WorkflowRun.SessionId</c>, dispatches to the source registry, and fails closed
/// cross-team. This is the highest fidelity the tool has (it has no HTTP / engine surface), so per the test taxonomy
/// it lives at the Integration tier — the same posture as <c>McpToolTeamScopeFlowTests</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class GetContextFlowTests
{
    private readonly PostgresFixture _fixture;

    public GetContextFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── SessionTurnsContextSource ────────────────────────────────────────────

    [Fact]
    public async Task Session_turns_returns_the_FULL_unclipped_result()
    {
        // The headline value vs the launch digest (which CLIPS each result to MaxResultChars): get_context returns the
        // whole thing back. Same source, no clip.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var huge = "FULL_" + new string('Z', SessionTurnText.MaxResultChars + 800);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "big", JsonSerializer.Serialize(new { summary = huge }));

        var result = await RetrieveTurnsAsync(teamId, sessionId);

        result.Found.ShouldBeTrue();
        result.Text.ShouldContain(huge, customMessage: "the FULL un-clipped prior result must come back — the whole point of the pull");
        result.Text.ShouldNotContain("…", customMessage: "get_context does NOT clip (unlike the digest)");
    }

    [Fact]
    public async Task Session_turns_query_filters_to_matching_turns()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "auth work", JsonSerializer.Serialize(new { summary = "did the AUTH refactor" }));
        await SeedTurnAsync(teamId, sessionId, turn: 2, goal: "logging", JsonSerializer.Serialize(new { summary = "added STRUCTURED logging" }));

        var result = await RetrieveTurnsAsync(teamId, sessionId, query: "auth");

        result.Found.ShouldBeTrue();
        result.Text.ShouldContain("AUTH refactor", customMessage: "the matching turn is returned");
        result.Text.ShouldNotContain("STRUCTURED logging", customMessage: "a non-matching turn is filtered out by the query");
    }

    [Fact]
    public async Task Session_turns_excludes_inherited_null_turn_runs()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "real", JsonSerializer.Serialize(new { summary = "TOP_LEVEL" }));
        await SeedTurnAsync(teamId, sessionId, turn: null, goal: "replay", JsonSerializer.Serialize(new { summary = "CHILD_HIDDEN" }));

        var result = await RetrieveTurnsAsync(teamId, sessionId);

        result.Text.ShouldContain("TOP_LEVEL");
        result.Text.ShouldNotContain("CHILD_HIDDEN", customMessage: "a child/replay run (null turn index) is not a thread turn");
    }

    [Fact]
    public async Task Session_turns_is_team_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "x", JsonSerializer.Serialize(new { summary = "OWNED_BY_A" }));

        (await RetrieveTurnsAsync(otherTeamId, sessionId)).Found
            .ShouldBeFalse("a foreign team must read none of this session's turns");
    }

    [Fact]
    public async Task Session_turns_with_no_session_is_a_clean_miss()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        (await RetrieveTurnsAsync(teamId, sessionId: null)).Found
            .ShouldBeFalse("a session-less run has no thread to read");
    }

    [Fact]
    public async Task Session_turns_bounds_the_output_and_notes_the_omission()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);

        // 10 turns × ~5KB each ≈ 50KB > the 40KB budget → the oldest matched turns are dropped (newest kept).
        for (var turn = 1; turn <= 10; turn++)
            await SeedTurnAsync(teamId, sessionId, turn, goal: $"g{turn}", JsonSerializer.Serialize(new { summary = $"MARKER_{turn}_" + new string('Z', 5000) }));

        var result = await RetrieveTurnsAsync(teamId, sessionId);

        result.Found.ShouldBeTrue();
        result.Text.Length.ShouldBeLessThanOrEqualTo(SessionTurnsContextSource.MaxOutputChars + 500, customMessage: "the output stays within the size budget (+ the heading/note overhead)");
        result.Text.ShouldContain("MARKER_10_", customMessage: "the most recent turn is always kept");
        result.Text.ShouldNotContain("MARKER_1_", customMessage: "the oldest turns are dropped to fit the budget");
        result.Text.ShouldContain("omitted", customMessage: "a budget drop is announced, never silent");
    }

    [Fact]
    public async Task Session_turns_clips_a_single_turn_that_alone_exceeds_the_budget()
    {
        // The newest turn is never dropped (a result must never be empty) — but a SINGLE un-clipped result bigger than
        // the whole budget (e.g. a large diff/file dump) must still be bounded, or one pull blows up the model's context.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var oversized = "BIGSTART_" + new string('Q', SessionTurnsContextSource.MaxOutputChars + 5000);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "huge", JsonSerializer.Serialize(new { summary = oversized }));

        var result = await RetrieveTurnsAsync(teamId, sessionId);

        result.Found.ShouldBeTrue("the newest turn is never dropped, even when oversized");
        result.Text.Length.ShouldBeLessThanOrEqualTo(SessionTurnsContextSource.MaxOutputChars + 200, customMessage: "a single giant turn is clipped to the budget (the IContextSource bound holds for one turn too)");
        result.Text.ShouldContain("BIGSTART_", customMessage: "the kept turn's content is present (clipped, not dropped)");
        result.Text.ShouldContain("truncated", customMessage: "the clip is announced, never silent");
    }

    // ─── SessionSummaryContextSource ──────────────────────────────────────────

    [Fact]
    public async Task Session_summary_returns_the_rolling_summary()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, summary: "EARLIER_WORK_DISTILLED");

        var result = await RetrieveSummaryAsync(teamId, sessionId);

        result.Found.ShouldBeTrue();
        result.Text.ShouldContain("EARLIER_WORK_DISTILLED");
    }

    [Fact]
    public async Task Session_summary_with_no_summary_yet_is_a_clean_miss()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, summary: null);

        (await RetrieveSummaryAsync(teamId, sessionId)).Found
            .ShouldBeFalse("a short thread carries no summary yet");
    }

    [Fact]
    public async Task Session_summary_is_team_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, summary: "OWNED_BY_A");

        (await RetrieveSummaryAsync(otherTeamId, sessionId)).Found
            .ShouldBeFalse("a foreign team must not read this thread's summary");
    }

    // ─── The assembled tool through the real handler + registry ───────────────

    [Fact]
    public async Task Tool_resolves_the_run_to_its_session_and_returns_full_turns()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var huge = "FULL_" + new string('Z', SessionTurnText.MaxResultChars + 800);
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "the work", JsonSerializer.Serialize(new { summary = huge }));
        var runId = await SeedAgentRunAsync(teamId, sessionId);

        var result = await CallToolAsync(teamId, runId, new { source = "session.turns" });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        var output = StructuredOutput(result);
        output.GetProperty("found").GetBoolean().ShouldBeTrue("the handler stamped the run id → the tool resolved AgentRun→WorkflowRun→session");
        output.GetProperty("source").GetString().ShouldBe("session.turns");
        output.GetProperty("text").GetString().ShouldContain(huge, customMessage: "the FULL un-clipped prior turn comes back end-to-end through the handler");
    }

    [Fact]
    public async Task Tool_with_no_source_pulls_every_source()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId, summary: "OLDER_WORK_SUMMARY");
        await SeedTurnAsync(teamId, sessionId, turn: 1, goal: "g", JsonSerializer.Serialize(new { summary = "RECENT_TURN_RESULT" }));
        var runId = await SeedAgentRunAsync(teamId, sessionId);

        var result = await CallToolAsync(teamId, runId, new { });

        var output = StructuredOutput(result);
        output.GetProperty("found").GetBoolean().ShouldBeTrue();
        output.GetProperty("source").GetString().ShouldBe("all");
        var text = output.GetProperty("text").GetString()!;
        text.ShouldContain("RECENT_TURN_RESULT", customMessage: "the turns source contributed");
        text.ShouldContain("OLDER_WORK_SUMMARY", customMessage: "the summary source contributed too — both were pulled");
    }

    [Fact]
    public async Task Tool_unknown_source_is_a_teachable_error_listing_what_exists()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        var runId = await SeedAgentRunAsync(teamId, sessionId);

        var result = await CallToolAsync(teamId, runId, new { source = "does.not.exist" });

        result.GetProperty("isError").GetBoolean().ShouldBeTrue("an unknown source is a usage error the model can correct");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        text.ShouldContain("does.not.exist");
        text.ShouldContain("session.turns", customMessage: "the error lists the available sources");
        text.ShouldContain("session.summary");
    }

    [Fact]
    public async Task Tool_for_a_session_less_run_is_a_clean_miss()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedAgentRunAsync(teamId, sessionId: null);   // a run not bound to any thread

        var result = await CallToolAsync(teamId, runId, new { source = "session.turns" });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse("a session-less run is a clean miss, not an error");
        StructuredOutput(result).GetProperty("found").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Tool_for_a_cross_team_run_finds_nothing()
    {
        // The run + its session belong to team A; a handler bound to team B names the run id → the team-scoped
        // AgentRun→WorkflowRun join resolves nothing → no session → a clean miss (no cross-tenant context leak).
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamA);
        await SeedTurnAsync(teamA, sessionId, turn: 1, goal: "g", JsonSerializer.Serialize(new { summary = "SECRET_OF_A" }));
        var runId = await SeedAgentRunAsync(teamA, sessionId);

        var result = await CallToolAsync(teamB, runId, new { source = "session.turns" });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        var output = StructuredOutput(result);
        output.GetProperty("found").GetBoolean().ShouldBeFalse("a cross-team run resolves no session — tenancy fail-closed");
        output.GetProperty("text").GetString().ShouldNotContain("SECRET_OF_A");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<AgentContextResult> RetrieveTurnsAsync(Guid teamId, Guid? sessionId, string? query = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<SessionTurnsContextSource>()
            .RetrieveAsync(new AgentContextQuery { TeamId = teamId, RunId = Guid.NewGuid(), SessionId = sessionId, Query = query }, CancellationToken.None);
    }

    private async Task<AgentContextResult> RetrieveSummaryAsync(Guid teamId, Guid? sessionId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<SessionSummaryContextSource>()
            .RetrieveAsync(new AgentContextQuery { TeamId = teamId, RunId = Guid.NewGuid(), SessionId = sessionId }, CancellationToken.None);
    }

    private async Task<JsonElement> CallToolAsync(Guid teamId, Guid runId, object arguments)
    {
        using var scope = _fixture.BeginScope();
        var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Confined, teamId, redactor: null, runId: runId);

        var request = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = GetContextTool.ToolKind, arguments } });
        var response = await handler.HandleAsync(request, CancellationToken.None);

        return response!.Value.GetProperty("result");
    }

    /// <summary>The tool declares an output schema, so the handler also returns the typed object under structuredContent.</summary>
    private static JsonElement StructuredOutput(JsonElement toolResult) => toolResult.GetProperty("structuredContent");

    private async Task<Guid> SeedSessionAsync(Guid teamId, string? summary = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.CreateVersion7();
        db.WorkSession.Add(new WorkSession { Id = id, TeamId = teamId, Title = "thread", Kind = WorkSessionKind.Task, Status = WorkSessionStatus.Open, Summary = summary });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedTurnAsync(Guid teamId, Guid sessionId, int? turn, string goal, string outputsJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, SessionId = sessionId, SessionTurnIndex = turn,
            OutputsJson = outputsJson, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Seed an AgentRun bound (soft link) to a fresh WorkflowRun carrying <paramref name="sessionId"/> — the run→session lineage get_context resolves.</summary>
    private async Task<Guid> SeedAgentRunAsync(Guid teamId, Guid? sessionId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });

        var workflowRunId = Guid.NewGuid();
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = workflowRunId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Running, SessionId = sessionId,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        var agentRunId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = workflowRunId, Harness = "codex-cli",
            Status = AgentRunStatus.Running, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return agentRunId;
    }
}
