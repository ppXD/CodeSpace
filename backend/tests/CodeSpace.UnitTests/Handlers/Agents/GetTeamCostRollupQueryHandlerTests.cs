using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

/// <summary>
/// Unit-proves <see cref="GetTeamCostRollupQueryHandler"/> is a thin dispatcher (Rule 16): it delegates to
/// <see cref="ITeamCostService.ComputeRollupAsync"/> scoped to the CALLER'S team (<see cref="ICurrentTeam"/>,
/// never the wire) with the since filter threaded through, and returns the service's rollup as-is. No DbContext —
/// the handler owns no data access. Mirrors <c>GetSupervisorScorecardQueryHandlerTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class GetTeamCostRollupQueryHandlerTests
{
    [Fact]
    public async Task Delegates_to_the_service_with_the_callers_team_and_the_since_filter()
    {
        var teamId = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var service = new CapturingService(AnyRollup());

        await new GetTeamCostRollupQueryHandler(service, new StubCurrentTeam(teamId))
            .Handle(new GetTeamCostRollupQuery { Since = since }, CancellationToken.None);

        service.LastTeamId.ShouldBe(teamId, "the handler must scope the bill to the CALLER's team (ICurrentTeam), never the wire");
        service.LastSince.ShouldBe(since, "the since window threads straight through");
    }

    [Fact]
    public async Task Returns_the_services_rollup_unchanged()
    {
        var rollup = AnyRollup();

        var result = await new GetTeamCostRollupQueryHandler(new CapturingService(rollup), new StubCurrentTeam(Guid.NewGuid()))
            .Handle(new GetTeamCostRollupQuery(), CancellationToken.None);

        result.ShouldBeSameAs(rollup, "the handler returns the service's rollup as-is — no re-projection");
    }

    private static TeamCostRollup AnyRollup() => new() { Runs = Array.Empty<RunCostSummary>() };

    private sealed class CapturingService : ITeamCostService
    {
        private readonly TeamCostRollup _rollup;

        public CapturingService(TeamCostRollup rollup) { _rollup = rollup; }

        public Guid LastTeamId { get; private set; }
        public DateTimeOffset? LastSince { get; private set; }

        public Task<TeamCostRollup> ComputeRollupAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken)
        {
            LastTeamId = teamId;
            LastSince = since;
            return Task.FromResult(_rollup);
        }

        public Task<RunCostSummary> ComputeRunAsync(Guid teamId, Guid workflowRunId, CancellationToken cancellationToken) =>
            Task.FromResult(new RunCostSummary { WorkflowRunId = workflowRunId });

        public Task<IReadOnlyDictionary<Guid, RunCostSummary>> ComputeRunsAsync(Guid teamId, IReadOnlyCollection<Guid> workflowRunIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, RunCostSummary>>(new Dictionary<Guid, RunCostSummary>());
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
