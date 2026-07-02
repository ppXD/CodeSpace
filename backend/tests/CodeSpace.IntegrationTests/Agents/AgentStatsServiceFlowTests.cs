using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High fidelity — the real <see cref="AgentStatsService"/> over real Postgres, real jsonb projection of
/// <c>AgentRun.TaskJson</c>/<c>ResultJson</c>, real pricing. Proves the per-agent evidence the redesigned Agents
/// roster reads is grouped by PERSONA, windowed, priced, and — the decisive promise — team-isolated. Pins: the
/// per-agent grouping, that runs without an <c>AgentDefinitionId</c> are excluded (they belong to no persona),
/// terminal-only success/latency vs the in-flight-inclusive sparkline, the fail-open unknown-cost qualifier, the
/// since window, and the production read path through the team-scoped query handler.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentStatsServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentStatsServiceFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Stats_are_grouped_per_persona_with_terminal_only_scoring_and_a_full_sparkline()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var alpha = Guid.NewGuid();
        var beta = Guid.NewGuid();

        // alpha: 2 succeeded + 1 failed (terminal) + 1 running (in-flight) → 66% over 3, 4 sparkline dots.
        await SeedAgentAsync(teamId, alpha, AgentRunStatus.Succeeded, ResultJson(1_000_000, 0), model: "claude-opus-4-8");
        await SeedAgentAsync(teamId, alpha, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8");
        await SeedAgentAsync(teamId, alpha, AgentRunStatus.Failed, ResultJson(0, 0), model: "claude-opus-4-8");
        await SeedAgentAsync(teamId, alpha, AgentRunStatus.Running, resultJson: null, model: "claude-opus-4-8");
        // beta: 1 succeeded.
        await SeedAgentAsync(teamId, beta, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-sonnet-4-6");

        var rollup = await ComputeAsync(teamId);

        rollup.Agents.Count.ShouldBe(2, "one row per persona with runs");

        var a = rollup.Agents.Single(s => s.AgentDefinitionId == alpha);
        a.Total.ShouldBe(3, "only the 3 terminal runs are scored — the running one is excluded from the total");
        a.Succeeded.ShouldBe(2);
        a.SuccessRate.ShouldBe(2.0 / 3.0, 1e-9);
        a.RecentOutcomes.Count.ShouldBe(4, "the sparkline keeps the in-flight run");
        a.RecentOutcomes.ShouldContain(AgentRunStatus.Running);
        a.LastRunAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5), "the last-active stamp is the most recent run's timestamp (interceptor-stamped to ~now)");

        var b = rollup.Agents.Single(s => s.AgentDefinitionId == beta);
        b.Total.ShouldBe(1);
        b.SuccessRate.ShouldBe(1.0);
        b.EstimatedCostUsd.ShouldBe(0m, "beta's one run priced (sonnet) at 0 tokens → a real $0, distinct from unknown/null");
        b.UnknownCostRuns.ShouldBe(0);
    }

    [Fact]
    public async Task Runs_without_a_persona_are_excluded()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var persona = Guid.NewGuid();

        await SeedAgentAsync(teamId, persona, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8");
        // A pure-inline run with NO AgentDefinitionId — belongs to no persona, must not appear.
        await SeedAgentAsync(teamId, agentDefinitionId: null, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8");

        var rollup = await ComputeAsync(teamId);

        rollup.Agents.Count.ShouldBe(1, "the persona-less run forms no row");
        rollup.Agents.Single().AgentDefinitionId.ShouldBe(persona);
    }

    [Fact]
    public async Task Per_persona_cost_sums_priced_runs_and_qualifies_the_unpriceable()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var priced = Guid.NewGuid();
        var unknown = Guid.NewGuid();

        // priced persona: opus 1M input → $5, plus a running row (no result) that must NOT count as unknown.
        await SeedAgentAsync(teamId, priced, AgentRunStatus.Succeeded, ResultJson(1_000_000, 0), model: "claude-opus-4-8");
        await SeedAgentAsync(teamId, priced, AgentRunStatus.Running, resultJson: null, model: "claude-opus-4-8");
        // unknown persona: a Codex model absent from the price table → eligible but unpriceable.
        await SeedAgentAsync(teamId, unknown, AgentRunStatus.Succeeded, ResultJson(5_000_000, 0), model: "gpt-5-codex");

        var rollup = await ComputeAsync(teamId);

        var p = rollup.Agents.Single(s => s.AgentDefinitionId == priced);
        p.EstimatedCostUsd.ShouldBe(5m, "$5 for the opus run; the in-flight run contributes nothing");
        p.UnknownCostRuns.ShouldBe(0, "the running row is not cost-eligible, so it is NOT counted as unknown");

        var u = rollup.Agents.Single(s => s.AgentDefinitionId == unknown);
        u.EstimatedCostUsd.ShouldBeNull("nothing priceable for this persona — null is distinct from a real $0");
        u.UnknownCostRuns.ShouldBe(1);
    }

    [Fact]
    public async Task A_team_never_sees_another_teams_agent_stats()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var personaA = Guid.NewGuid();
        var personaB = Guid.NewGuid();

        await SeedAgentAsync(teamA, personaA, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8");
        await SeedAgentAsync(teamB, personaB, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8");

        var rollupA = await ComputeAsync(teamA);
        rollupA.Agents.ShouldContain(s => s.AgentDefinitionId == personaA);
        rollupA.Agents.ShouldNotContain(s => s.AgentDefinitionId == personaB, "team B's persona must NEVER enter team A's stats (tenancy fail-closed)");
    }

    [Fact]
    public async Task The_since_window_excludes_runs_created_before_the_horizon()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var persona = Guid.NewGuid();

        await SeedAgentAsync(teamId, persona, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8", createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        await SeedAgentAsync(teamId, persona, AgentRunStatus.Failed, ResultJson(0, 0), model: "claude-opus-4-8", createdAt: DateTimeOffset.UtcNow.AddDays(-30));

        using var scope = _fixture.BeginScope();
        var rollup = await scope.Resolve<IAgentStatsService>().ComputeAsync(teamId, since: DateTimeOffset.UtcNow.AddDays(-7), CancellationToken.None);

        var s = rollup.Agents.Single(x => x.AgentDefinitionId == persona);
        s.Total.ShouldBe(1, "only the 1-hour-old run is inside the 7-day window; the 30-day-old failure is excluded");
        s.Succeeded.ShouldBe(1, "the excluded run was the failure — proving the CreatedDate >= from predicate is real, not inverted");
    }

    [Fact]
    public async Task The_team_scoped_query_handler_returns_only_the_callers_team_stats()
    {
        // The production read path: GetAgentStatsQuery resolves the team from ICurrentTeam (the X-Team-Id header),
        // NEVER the wire — the controller's exact dispatch.
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var personaA = Guid.NewGuid();
        var personaB = Guid.NewGuid();

        await SeedAgentAsync(teamA, personaA, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8");
        await SeedAgentAsync(teamB, personaB, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8");

        using var scope = _fixture.BeginScopeAs(userA, teamA, Roles.Admin);
        var rollup = await scope.Resolve<IMediator>().Send(new GetAgentStatsQuery { Since = null });

        rollup.Agents.ShouldContain(s => s.AgentDefinitionId == personaA);
        rollup.Agents.ShouldNotContain(s => s.AgentDefinitionId == personaB, "the handler scopes to the caller's team via ICurrentTeam — team B's persona is invisible");
    }

    [Fact]
    public async Task A_malformed_result_row_degrades_to_unknown_without_crashing()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var persona = Guid.NewGuid();

        await SeedAgentAsync(teamId, persona, AgentRunStatus.Succeeded, ResultJson(1_000_000, 0), model: "claude-opus-4-8");   // $5
        // Valid jsonb, wrong shape for AgentRunResult (an array) — the defensive TryDeserialize must tolerate it.
        await SeedAgentAsync(teamId, persona, AgentRunStatus.Succeeded, resultJson: "[]", model: "claude-opus-4-8");

        var rollup = await ComputeAsync(teamId);

        var s = rollup.Agents.Single();
        s.Total.ShouldBe(2, "both terminal runs count — the roll-up completes");
        s.EstimatedCostUsd.ShouldBe(5m, "the good run still prices; the corrupt row degrades to unknown rather than crashing");
        s.UnknownCostRuns.ShouldBe(1, "the malformed row is surfaced as unknown-cost");
    }

    [Fact]
    public async Task Latency_percentiles_are_computed_from_the_run_timestamps()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var persona = Guid.NewGuid();

        // A terminal run whose CompletedAt is 10s after StartedAt → duration 10s. CreatedDate is set 5s EARLIER than
        // StartedAt so a mutant deriving the duration from CreatedDate (or flipping the subtraction sign) yields a
        // different value (or a negative that the >= 0 filter drops), and this assertion catches it.
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        await SeedAgentAsync(teamId, persona, AgentRunStatus.Succeeded, ResultJson(0, 0), model: "claude-opus-4-8",
            createdAt: started.AddSeconds(-5), startedAt: started, completedAt: started.AddSeconds(10));

        var stat = (await ComputeAsync(teamId)).Agents.Single();

        stat.P50DurationSeconds.ShouldBe(10, "the duration is CompletedAt - StartedAt = 10s (not the CreatedAt-based span, not sign-flipped, not ms)");
        stat.P95DurationSeconds.ShouldBe(10);
    }

    [Fact]
    public async Task A_terminal_run_with_no_result_is_excluded_from_cost_not_counted_as_unknown()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var persona = Guid.NewGuid();

        // A priceable opus success ($5) and a Failed run that persisted NO result (crashed before writing one). The
        // resultless failure is TERMINAL (it counts toward Total) but NOT cost-eligible — cost eligibility keys on the
        // presence of a result (ResultJson != null), NOT on terminal status. A mutant using IsTerminal(status) for
        // eligibility would price the resultless row as unknown and inflate UnknownCostRuns; this pins it out.
        await SeedAgentAsync(teamId, persona, AgentRunStatus.Succeeded, ResultJson(1_000_000, 0), model: "claude-opus-4-8");
        await SeedAgentAsync(teamId, persona, AgentRunStatus.Failed, resultJson: null, model: "claude-opus-4-8");

        var stat = (await ComputeAsync(teamId)).Agents.Single();

        stat.Total.ShouldBe(2, "both runs are terminal — the resultless failure still counts toward the success denominator");
        stat.Succeeded.ShouldBe(1);
        stat.EstimatedCostUsd.ShouldBe(5m, "only the run WITH a result is priced");
        stat.UnknownCostRuns.ShouldBe(0, "the resultless terminal run is NOT cost-eligible, so it is excluded — never counted as unknown-cost");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<AgentStatsRollup> ComputeAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentStatsService>().ComputeAsync(teamId, since: null, CancellationToken.None);
    }

    private async Task SeedAgentAsync(Guid teamId, Guid? agentDefinitionId, AgentRunStatus status, string? resultJson, string model,
        DateTimeOffset? createdAt = null, DateTimeOffset? startedAt = null, DateTimeOffset? completedAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = Guid.NewGuid(),
            AgentDefinitionId = agentDefinitionId,
            Harness = "claude-code",
            Status = status,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "g", Harness = "claude-code", Model = model }, AgentJson.Options),
            ResultJson = resultJson,
            // Started/Completed are the timestamps the service's duration projection reads (CompletedAt - StartedAt);
            // nullable columns, so a test that doesn't care leaves them null (duration null → excluded from latency).
            StartedAt = startedAt,
            CompletedAt = completedAt,
            // The auditing interceptor only stamps CreatedDate when it is default, so an explicit value survives
            // (the since-window test relies on it).
            CreatedDate = createdAt ?? default,
        });

        await db.SaveChangesAsync();
    }

    private static string ResultJson(int input, int output) => JsonSerializer.Serialize(new AgentRunResult
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output },
    }, AgentJson.Options);
}
