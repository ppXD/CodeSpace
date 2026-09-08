using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Context.Sources;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

public partial class GetContextFlowTests
{
    [Fact]
    [Trait("P17", "Regression")]
    public async Task Session_turn_pages_resume_across_fresh_scopes_without_duplicates_or_gaps()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= SessionTurnsContextSource.MaxTurnsScanned + 11; turn++)
            await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", JsonSerializer.Serialize(new { summary = $"RESULT_{turn}" }));
        var runId = await SeedAgentRunAsync(teamId, sessionId);

        var first = StructuredOutput(await CallToolAsync(teamId, runId, new { }));
        first.GetProperty("source").GetString().ShouldBe("all", "an aggregate discovery call must preserve each partial source's continuation");
        first.GetProperty("coverage").GetString().ShouldBe("partial");
        var cursor = SingleCursor(first, "session.turns");

        var second = StructuredOutput(await CallToolAsync(teamId, runId, new { source = "session.turns", cursor }));
        second.GetProperty("coverage").GetString().ShouldBe("complete");
        second.GetProperty("continuations").GetArrayLength().ShouldBe(0);

        var turns = TurnNumbers(first).Concat(TurnNumbers(second)).ToList();
        turns.Count.ShouldBe(SessionTurnsContextSource.MaxTurnsScanned + 11);
        turns.Distinct().Order().ShouldBe(Enumerable.Range(1, SessionTurnsContextSource.MaxTurnsScanned + 11));
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task A_body_budget_cut_can_be_resumed_until_every_matching_turn_is_covered()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= 10; turn++)
            await SeedTurnAsync(teamId, sessionId, turn, "budget", JsonSerializer.Serialize(new { summary = $"MATCH_{turn}_" + new string('x', 5_000) }));
        var runId = await SeedAgentRunAsync(teamId, sessionId);
        var seen = new List<int>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var arguments = cursor == null ? new { source = "session.turns", query = "MATCH_" } : (object)new { source = "session.turns", query = "MATCH_", cursor };
            var output = StructuredOutput(await CallToolAsync(teamId, runId, arguments));
            seen.AddRange(TurnNumbers(output));
            var continuations = output.GetProperty("continuations");
            if (continuations.GetArrayLength() == 0)
            {
                output.GetProperty("coverage").GetString().ShouldBe("complete");
                break;
            }

            output.GetProperty("coverage").GetString().ShouldBe("partial");
            cursor = SingleCursor(output, "session.turns");
        }

        seen.Count.ShouldBe(10);
        seen.Distinct().Order().ShouldBe(Enumerable.Range(1, 10));
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task Malformed_or_rebound_cursors_fail_loud_instead_of_restarting_or_crossing_scope()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionA = await SeedSessionAsync(teamA);
        var sessionB = await SeedSessionAsync(teamB);
        for (var turn = 1; turn <= SessionTurnsContextSource.MaxTurnsScanned + 1; turn++)
            await SeedTurnAsync(teamA, sessionA, turn, "alpha", JsonSerializer.Serialize(new { summary = $"SECRET_A_{turn}" }));
        var runA = await SeedAgentRunAsync(teamA, sessionA);
        var runB = await SeedAgentRunAsync(teamB, sessionB);
        var first = StructuredOutput(await CallToolAsync(teamA, runA, new { source = "session.turns", query = "alpha" }));
        var cursor = SingleCursor(first, "session.turns");

        (await CallToolAsync(teamA, runA, new { source = "session.turns", cursor = "not-a-valid-cursor" })).GetProperty("isError").GetBoolean().ShouldBeTrue();
        (await CallToolAsync(teamA, runA, new { source = "session.turns", query = "beta", cursor })).GetProperty("isError").GetBoolean().ShouldBeTrue("a cursor is bound to the refinement that produced it");
        var foreign = await CallToolAsync(teamB, runB, new { source = "session.turns", query = "alpha", cursor });
        foreign.GetProperty("isError").GetBoolean().ShouldBeTrue("a cursor is bound to the trusted team and session scope that produced it");
        foreign.GetRawText().ShouldNotContain("SECRET_A_");
    }

    private static string SingleCursor(JsonElement output, string source)
    {
        var continuation = output.GetProperty("continuations").EnumerateArray().ShouldHaveSingleItem();
        continuation.GetProperty("source").GetString().ShouldBe(source);
        var cursor = continuation.GetProperty("cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();
        return cursor!;
    }

    private static IEnumerable<int> TurnNumbers(JsonElement output) =>
        Regex.Matches(output.GetProperty("text").GetString() ?? "", @"^## Turn (\d+) ", RegexOptions.Multiline)
            .Select(match => int.Parse(match.Groups[1].Value));
}
