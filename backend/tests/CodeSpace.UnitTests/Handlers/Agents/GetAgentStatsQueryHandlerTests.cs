using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

/// <summary>
/// Unit-proves <see cref="GetAgentStatsQueryHandler"/> is a thin dispatcher (Rule 16): it delegates to
/// <see cref="IAgentStatsService.ComputeAsync"/> with the CALLER'S team (<see cref="ICurrentTeam"/>, never the
/// wire) and the request's since window threaded straight through, and returns the service's
/// <see cref="AgentStatsRollup"/> as-is. No DbContext — the handler owns no data access. Mirrors
/// <see cref="GetAgentScorecardQueryHandlerTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class GetAgentStatsQueryHandlerTests
{
    [Fact]
    public async Task Delegates_to_the_service_with_the_callers_team_and_the_since_window()
    {
        var teamId = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var service = new CapturingStatsService(AnyRollup());

        await new GetAgentStatsQueryHandler(service, new StubCurrentTeam(teamId))
            .Handle(new GetAgentStatsQuery { Since = since }, CancellationToken.None);

        service.LastTeamId.ShouldBe(teamId, "the handler must scope the stats to the CALLER's team (ICurrentTeam), not the wire");
        service.LastSince.ShouldBe(since, "the since window must thread straight through");
    }

    [Fact]
    public async Task Returns_the_services_rollup_unchanged()
    {
        var rollup = new AgentStatsRollup
        {
            Agents = new[]
            {
                new AgentStat
                {
                    AgentDefinitionId = Guid.NewGuid(),
                    Total = 4, Succeeded = 3, SuccessRate = 0.75,
                    P50DurationSeconds = 20, P95DurationSeconds = 30,
                    EstimatedCostUsd = 1.94m, UnknownCostRuns = 0,
                    LastRunAt = DateTimeOffset.UtcNow,
                    RecentOutcomes = Array.Empty<Messages.Enums.AgentRunStatus>(),
                },
            },
        };

        var result = await new GetAgentStatsQueryHandler(new CapturingStatsService(rollup), new StubCurrentTeam(Guid.NewGuid()))
            .Handle(new GetAgentStatsQuery(), CancellationToken.None);

        result.ShouldBeSameAs(rollup, "the handler returns the service's rollup as-is — no re-projection");
        result.Agents.Single().SuccessRate.ShouldBe(0.75);
    }

    private static AgentStatsRollup AnyRollup() => new() { Agents = Array.Empty<AgentStat>() };

    /// <summary>An IAgentStatsService double that records the call args + returns a canned rollup — no DbContext (proving the handler owns no data access).</summary>
    private sealed class CapturingStatsService : IAgentStatsService
    {
        private readonly AgentStatsRollup _rollup;

        public CapturingStatsService(AgentStatsRollup rollup) { _rollup = rollup; }

        public Guid LastTeamId { get; private set; }
        public DateTimeOffset? LastSince { get; private set; }

        public Task<AgentStatsRollup> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
        {
            LastTeamId = teamId;
            LastSince = since;
            return Task.FromResult(_rollup);
        }
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
