using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// End-to-end for <see cref="IAgentRunScorecardService"/> against real Postgres: it must aggregate a team's
/// real AgentRun history into per-harness success/latency scores, stay strictly team-scoped (no cross-tenant
/// leakage — the B2B promise), and honor the harness filter.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunScorecardFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunScorecardFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Aggregates_terminal_runs_per_harness_with_success_rate_and_duration()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);

        await SeedRunsAsync(teamId,
            ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(10)),
            ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(30)),
            ("codex-cli", AgentRunStatus.Failed,    t0, t0.AddSeconds(20)),
            ("claude-code", AgentRunStatus.Succeeded, t0, t0.AddSeconds(5)),
            ("codex-cli", AgentRunStatus.Running,   t0, null));   // in-flight → excluded

        var card = await ComputeAsync(teamId, since: null, harness: null);

        card.Overall.Total.ShouldBe(4, "the in-flight run is not scored");
        card.Overall.Succeeded.ShouldBe(3);
        card.Overall.SuccessRate.ShouldBe(0.75);

        card.Harnesses.Count.ShouldBe(2);
        var codex = card.Harnesses.Single(h => h.Harness == "codex-cli");
        codex.Total.ShouldBe(3);
        codex.Succeeded.ShouldBe(2);
        codex.P50DurationSeconds.ShouldNotBeNull();
        codex.P95DurationSeconds!.Value.ShouldBe(30, 0.5, "the slowest codex run was 30s");

        card.Harnesses.Single(h => h.Harness == "claude-code").SuccessRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task Is_strictly_team_scoped()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);

        await SeedRunsAsync(teamA, ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(10)));
        await SeedRunsAsync(teamB, ("codex-cli", AgentRunStatus.Failed, t0, t0.AddSeconds(10)), ("codex-cli", AgentRunStatus.Failed, t0, t0.AddSeconds(10)));

        var cardA = await ComputeAsync(teamA, since: null, harness: null);

        cardA.Overall.Total.ShouldBe(1, "team B's runs must never enter team A's scorecard");
        cardA.Overall.SuccessRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task Filters_to_a_single_harness()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);

        await SeedRunsAsync(teamId,
            ("codex-cli", AgentRunStatus.Succeeded, t0, t0.AddSeconds(10)),
            ("claude-code", AgentRunStatus.Failed, t0, t0.AddSeconds(10)));

        var card = await ComputeAsync(teamId, since: null, harness: "codex-cli");

        card.Harnesses.Count.ShouldBe(1);
        card.Harnesses[0].Harness.ShouldBe("codex-cli");
        card.Overall.Total.ShouldBe(1);
    }

    private async Task<Messages.Agents.AgentRunScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, string? harness)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunScorecardService>().ComputeAsync(teamId, since, harness, CancellationToken.None);
    }

    private async Task SeedRunsAsync(Guid teamId, params (string Harness, AgentRunStatus Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt)[] runs)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var r in runs)
            db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, Harness = r.Harness, Status = r.Status, StartedAt = r.StartedAt, CompletedAt = r.CompletedAt });

        await db.SaveChangesAsync();
    }
}
