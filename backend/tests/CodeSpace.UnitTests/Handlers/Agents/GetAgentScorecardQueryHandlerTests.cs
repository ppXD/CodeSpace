using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

/// <summary>
/// Unit-proves <see cref="GetAgentScorecardQueryHandler"/> is a thin dispatcher (Rule 16): it delegates to
/// <see cref="IAgentRunScorecardService.ComputeAsync"/> with the CALLER'S team (<see cref="ICurrentTeam"/>,
/// never the wire) and the request's since/harness filters threaded straight through, and returns the
/// scorer's <see cref="AgentRunScorecard"/> as-is (no re-projection, no fabricated fields). No DbContext
/// anywhere — the handler owns no data access.
/// </summary>
[Trait("Category", "Unit")]
public class GetAgentScorecardQueryHandlerTests
{
    [Fact]
    public async Task Delegates_to_the_service_with_the_callers_team_and_the_filters()
    {
        var teamId = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var service = new CapturingScorecardService(AnyCard());

        await new GetAgentScorecardQueryHandler(service, new StubCurrentTeam(teamId))
            .Handle(new GetAgentScorecardQuery { Since = since, Harness = "codex-cli" }, CancellationToken.None);

        service.LastTeamId.ShouldBe(teamId, "the handler must scope the score to the CALLER's team (ICurrentTeam), not the wire");
        service.LastSince.ShouldBe(since, "the since filter must thread straight through");
        service.LastHarness.ShouldBe("codex-cli", "the harness filter must thread straight through");
    }

    [Fact]
    public async Task Returns_the_scorers_card_unchanged()
    {
        var card = new AgentRunScorecard
        {
            Overall = new HarnessScore { Harness = "(all)", Total = 4, Succeeded = 3, SuccessRate = 0.75, P50DurationSeconds = 20, P95DurationSeconds = 30 },
            Harnesses = new[]
            {
                new HarnessScore { Harness = "claude-code", Total = 1, Succeeded = 1, SuccessRate = 1.0, P50DurationSeconds = 5, P95DurationSeconds = 5 },
                new HarnessScore { Harness = "codex-cli", Total = 3, Succeeded = 2, SuccessRate = 2.0 / 3, P50DurationSeconds = 20, P95DurationSeconds = 30 },
            },
        };

        var result = await new GetAgentScorecardQueryHandler(new CapturingScorecardService(card), new StubCurrentTeam(Guid.NewGuid()))
            .Handle(new GetAgentScorecardQuery(), CancellationToken.None);

        result.ShouldBeSameAs(card, "the handler returns the scorer's card as-is — no re-projection");
        result.Overall.SuccessRate.ShouldBe(0.75);
        result.Overall.P95DurationSeconds.ShouldBe(30);
        result.Harnesses.Select(h => h.Harness).ShouldBe(new[] { "claude-code", "codex-cli" });
        result.Harnesses.Single(h => h.Harness == "codex-cli").Succeeded.ShouldBe(2);
    }

    private static AgentRunScorecard AnyCard() => new()
    {
        Overall = new HarnessScore { Harness = "(all)", Total = 0, Succeeded = 0, SuccessRate = 0 },
        Harnesses = Array.Empty<HarnessScore>(),
    };

    /// <summary>An IAgentRunScorecardService double that records the call args + returns a canned card — no DbContext (proving the handler owns no data access).</summary>
    private sealed class CapturingScorecardService : IAgentRunScorecardService
    {
        private readonly AgentRunScorecard _card;

        public CapturingScorecardService(AgentRunScorecard card) { _card = card; }

        public Guid LastTeamId { get; private set; }
        public DateTimeOffset? LastSince { get; private set; }
        public string? LastHarness { get; private set; }

        public Task<AgentRunScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, string? harness, CancellationToken cancellationToken)
        {
            LastTeamId = teamId;
            LastSince = since;
            LastHarness = harness;
            return Task.FromResult(_card);
        }
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
