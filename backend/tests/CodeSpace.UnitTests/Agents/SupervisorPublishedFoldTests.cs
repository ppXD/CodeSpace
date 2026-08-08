using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PURE published-agent fold the live gate's rehydrate AND the I3 auditor now share — one predicate,
/// two callers, so the auditor can never again indict the gate over a ledger it read differently (run 31230410920's
/// false "I3 did not hold"). Pins the all-or-nothing multi-repo law and the Pushed-or-PR alternative.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPublishedFoldTests
{
    [Fact]
    public void An_agent_is_published_only_when_every_repo_row_is()
    {
        var agent = Guid.NewGuid();
        var rows = new List<PublishManifest>
        {
            Row(agent, PublishState.Pushed),
            Row(agent, PublishState.PatchOnly),   // one repo lagging — the agent is NOT published
        };

        SupervisorTurnService.FoldPublishedAgentRunIds(rows).ShouldBeEmpty("all-or-nothing per multi-repo agent");

        rows[1].PublishStateValue = PublishState.Pushed;
        SupervisorTurnService.FoldPublishedAgentRunIds(rows).ShouldBe(new[] { agent });
    }

    [Fact]
    public void A_pull_request_counts_as_published_even_without_a_pushed_state()
    {
        var agent = Guid.NewGuid();
        var row = Row(agent, PublishState.PatchOnly);
        row.PullRequestNumber = 7;

        SupervisorTurnService.FoldPublishedAgentRunIds(new List<PublishManifest> { row }).ShouldBe(new[] { agent });
    }

    [Fact]
    public void Agent_less_rows_are_ignored()
    {
        SupervisorTurnService.FoldPublishedAgentRunIds(new List<PublishManifest> { Row(agentRunId: null, PublishState.Pushed) }).ShouldBeEmpty();
    }

    private static PublishManifest Row(Guid? agentRunId, PublishState state) => new()
    {
        Id = Guid.NewGuid(), AgentRunId = agentRunId, PublishStateValue = state, RepositoryAlias = "primary",
    };
}
