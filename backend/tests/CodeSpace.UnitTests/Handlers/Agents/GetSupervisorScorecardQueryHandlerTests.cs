using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

/// <summary>
/// Unit-proves <see cref="GetSupervisorScorecardQueryHandler"/> is a thin dispatcher (Rule 16): it delegates to
/// <see cref="ISupervisorScorecardService.ComputeAsync"/> with the CALLER'S team (<see cref="ICurrentTeam"/>,
/// never the wire) and the request's since filter threaded straight through, and returns the scorer's
/// <see cref="SupervisorScorecard"/> as-is. No DbContext anywhere — the handler owns no data access.
/// </summary>
[Trait("Category", "Unit")]
public class GetSupervisorScorecardQueryHandlerTests
{
    [Fact]
    public async Task Delegates_to_the_service_with_the_callers_team_and_the_since_filter()
    {
        var teamId = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var service = new CapturingService(AnyCard());

        await new GetSupervisorScorecardQueryHandler(service, new StubCurrentTeam(teamId))
            .Handle(new GetSupervisorScorecardQuery { Since = since }, CancellationToken.None);

        service.LastTeamId.ShouldBe(teamId, "the handler must scope the score to the CALLER's team (ICurrentTeam), not the wire");
        service.LastSince.ShouldBe(since, "the since filter must thread straight through");
    }

    [Fact]
    public async Task Returns_the_scorers_card_unchanged()
    {
        var card = AnyCard();

        var result = await new GetSupervisorScorecardQueryHandler(new CapturingService(card), new StubCurrentTeam(Guid.NewGuid()))
            .Handle(new GetSupervisorScorecardQuery(), CancellationToken.None);

        result.ShouldBeSameAs(card, "the handler returns the scorer's card as-is — no re-projection");
    }

    private static SupervisorScorecard AnyCard() => new()
    {
        Rollup = new SupervisorRollup { ScoredRuns = 0, NotScoredRuns = 0, AvgDecisionsPerRun = 0, AvgReplanRounds = 0, OverallSpawnSuccessRate = 0, OutcomeDistribution = new Dictionary<string, int>() },
        Runs = Array.Empty<SupervisorRunScore>(),
    };

    private sealed class CapturingService : ISupervisorScorecardService
    {
        private readonly SupervisorScorecard _card;

        public CapturingService(SupervisorScorecard card) { _card = card; }

        public Guid LastTeamId { get; private set; }
        public DateTimeOffset? LastSince { get; private set; }

        public Task<SupervisorScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
        {
            LastTeamId = teamId;
            LastSince = since;
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
