using System.Text.Json;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

public partial class GetContextFlowTests
{
    [Fact]
    [Trait("P17", "Regression")]
    public async Task The_production_context_tool_finds_an_exact_keyword_older_than_fifty_turns_after_restart()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var sessionId = await SeedSessionAsync(teamId);
        const string evidence = "EXACT_RECORDED_OLD_EVIDENCE_7826";
        await SeedTurnAsync(teamId, sessionId, 1, "old obligation", JsonSerializer.Serialize(new { summary = evidence }));
        for (var turn = 2; turn <= 61; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"unrelated-{turn}", JsonSerializer.Serialize(new { summary = "unrelated result" }));
        var agentRunId = await SeedAgentRunAsync(teamId, sessionId);

        var result = await CallToolAsync(teamId, agentRunId, new { source = "session.turns", query = evidence });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        var output = StructuredOutput(result);
        output.GetProperty("found").GetBoolean().ShouldBeTrue("searching the preserved source must precede limiting the page; old evidence is not absent");
        output.GetProperty("text").GetString().ShouldContain(evidence);
        output.GetProperty("text").GetString().ShouldContain("Turn 1");
        output.GetProperty("text").GetString().ShouldNotContain("unrelated result");
    }
}
