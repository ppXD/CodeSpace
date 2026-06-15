using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The scorecard API surface, end-to-end through the mediator + real Postgres (mirrors
/// <see cref="ToolCallAuditFlowTests"/>: the full pipeline IS the API-flow here, since the API project has
/// no HTTP test host — <c>AgentsController.GetScorecard</c> is a one-line <c>_mediator.Send(query)</c>).
///
/// <para>Proves the operator-facing contract on REAL computed numbers (not stubs): an owning team reads a
/// per-harness + overall scorecard whose success rate + latency percentiles come straight from the seeded
/// AgentRun history; the since/harness filters narrow the window; and a DIFFERENT team sees ONLY its own
/// runs — the tenancy proof. The team comes from <c>ICurrentTeam</c> (the X-Team-Id header surrogate the
/// fixture injects via BeginScopeAs), never the query string.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentScorecardApiFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentScorecardApiFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_owning_team_reads_a_scorecard_with_the_real_computed_success_rate_and_latency()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);

        await SeedRunsAsync(teamId,
            ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(10)),
            ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(30)),
            ("codex-cli", AgentRunStatus.Failed,    t0, t0.AddSeconds(20)),
            ("claude-code", AgentRunStatus.Succeeded, t0, t0.AddSeconds(5)),
            ("codex-cli", AgentRunStatus.Running,   t0, null));   // in-flight → excluded

        var card = await GetScorecardAsync(userId, teamId, new GetAgentScorecardQuery());

        // Overall rollup — REAL numbers: 3 of 4 terminal runs succeeded (the Running run is not scored).
        card.Overall.Total.ShouldBe(4, "the in-flight run is not scored");
        card.Overall.Succeeded.ShouldBe(3);
        card.Overall.SuccessRate.ShouldBe(0.75, "3 of 4 terminal runs succeeded — the real computed rate");

        // Per-harness breakdown, sorted by harness name (claude-code, codex-cli).
        card.Harnesses.Select(h => h.Harness).ShouldBe(new[] { "claude-code", "codex-cli" });

        var codex = card.Harnesses.Single(h => h.Harness == "codex-cli");
        codex.Total.ShouldBe(3);
        codex.Succeeded.ShouldBe(2);
        codex.P50DurationSeconds.ShouldNotBeNull("the median latency is a real number, computed from the seeded durations");
        codex.P95DurationSeconds!.Value.ShouldBe(30, 0.5, "the slowest codex run was 30s — the real P95");

        card.Harnesses.Single(h => h.Harness == "claude-code").SuccessRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task A_different_team_sees_only_its_own_runs_tenancy()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);

        await SeedRunsAsync(teamA, ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(10)));
        await SeedRunsAsync(teamB,
            ("codex-cli", AgentRunStatus.Failed, t0, t0.AddSeconds(10)),
            ("codex-cli", AgentRunStatus.Failed, t0, t0.AddSeconds(10)));

        // Team A reads its own scorecard — its single run, 100% — with NONE of team B's failing runs.
        var cardA = await GetScorecardAsync(userA, teamA, new GetAgentScorecardQuery());
        cardA.Overall.Total.ShouldBe(1, "team B's runs must never enter team A's scorecard");
        cardA.Overall.SuccessRate.ShouldBe(1.0);

        // Team B reads its OWN scorecard — two failing runs, 0% — proving each tenant sees only its own.
        var cardB = await GetScorecardAsync(userB, teamB, new GetAgentScorecardQuery());
        cardB.Overall.Total.ShouldBe(2);
        cardB.Overall.SuccessRate.ShouldBe(0.0, "team B sees only its own two failed runs");
    }

    [Fact]
    public async Task The_harness_filter_narrows_the_scorecard()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);

        await SeedRunsAsync(teamId,
            ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(10)),
            ("claude-code", AgentRunStatus.Failed, t0, t0.AddSeconds(10)));

        var card = await GetScorecardAsync(userId, teamId, new GetAgentScorecardQuery { Harness = "codex-cli" });

        card.Harnesses.Count.ShouldBe(1);
        card.Harnesses[0].Harness.ShouldBe("codex-cli");
        card.Overall.Total.ShouldBe(1, "only the codex run is in scope");
    }

    [Fact]
    public async Task The_since_filter_windows_the_scorecard()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var recent = DateTimeOffset.UtcNow.AddHours(-1);

        await SeedRunsAsync(teamId,
            ("codex-cli", AgentRunStatus.Failed, old, old.AddSeconds(10)),      // before the window
            ("codex-cli", AgentRunStatus.Succeeded, recent, recent.AddSeconds(10)));

        var card = await GetScorecardAsync(userId, teamId, new GetAgentScorecardQuery { Since = DateTimeOffset.UtcNow.AddDays(-7) });

        card.Overall.Total.ShouldBe(1, "only the run inside the since-window is scored");
        card.Overall.SuccessRate.ShouldBe(1.0);
    }

    private async Task<AgentRunScorecard> GetScorecardAsync(Guid userId, Guid teamId, GetAgentScorecardQuery query)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(query);
    }

    private async Task SeedRunsAsync(Guid teamId, params (string Harness, AgentRunStatus Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt)[] runs)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var r in runs)
            db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = r.Harness, Status = r.Status, StartedAt = r.StartedAt, CompletedAt = r.CompletedAt, CreatedDate = r.StartedAt });

        await db.SaveChangesAsync();
    }
}
