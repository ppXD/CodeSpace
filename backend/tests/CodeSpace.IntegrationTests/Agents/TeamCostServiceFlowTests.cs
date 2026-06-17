using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
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
/// 🟢 SOTA #4 read-plane crown jewel (high fidelity — real <see cref="TeamCostService"/> over real Postgres, real
/// jsonb projection of <c>AgentRun.TaskJson</c>/<c>ResultJson</c>, real pricing). Proves the captured-but-once-dead
/// <c>TokenUsage</c> rolls up into an auditable per-team bill, and — the decisive promise — that one team's spend
/// is NEVER visible to another (tenancy fail-closed). Also pins: terminal-only (a still-running agent with no
/// result is excluded), the fail-open unknown-cost qualifier (an unpriceable model is surfaced, never silently $0),
/// per-run breakdown, and the production read path through the team-scoped query handler.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamCostServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public TeamCostServiceFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_rollup_sums_priced_terminal_runs_and_qualifies_unpriceable_ones()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var run1 = Guid.NewGuid();
        var run2 = Guid.NewGuid();
        var run3 = Guid.NewGuid();

        // Run 1: two opus agents, 1M input each → $5 each → $10, 2M input.
        await SeedTerminalAgentAsync(teamId, run1, model: "claude-opus-4-8", input: 1_000_000, output: 0);
        await SeedTerminalAgentAsync(teamId, run1, model: "claude-opus-4-8", input: 1_000_000, output: 0);
        // Run 2: one sonnet agent, 1M input → $3.
        await SeedTerminalAgentAsync(teamId, run2, model: "claude-sonnet-4-6", input: 1_000_000, output: 0);
        // Run 3: an UNKNOWN model (Codex, absent from the default table) → priced as unknown (0), but its tokens still sum.
        await SeedTerminalAgentAsync(teamId, run3, model: "gpt-5-codex", input: 5_000_000, output: 0);
        // A still-RUNNING agent (no ResultJson) → must be EXCLUDED entirely (terminal-only).
        await SeedRunningAgentAsync(teamId, Guid.NewGuid(), model: "claude-opus-4-8");

        var rollup = await ComputeRollupAsync(teamId);

        rollup.TotalInputTokens.ShouldBe(8_000_000, "2M (run1) + 1M (run2) + 5M (run3 unknown) — the running agent is excluded");
        rollup.TotalOutputTokens.ShouldBe(0);
        rollup.EstimatedCostUsd.ShouldBe(13m, "$10 (run1 opus) + $3 (run2 sonnet); the unknown-model run contributes 0 to the priced sum");
        rollup.RunCount.ShouldBe(3, "three distinct terminal runs");
        rollup.WindowRunCount.ShouldBe(3);
        rollup.UnknownCostRuns.ShouldBe(1, "the one unpriceable (Codex) agent row is surfaced as unknown-cost, not silently zeroed");
        rollup.Truncated.ShouldBeFalse();
        rollup.Runs.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_team_never_sees_another_teams_spend()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        await SeedTerminalAgentAsync(teamA, runA, model: "claude-opus-4-8", input: 1_000_000, output: 0);   // $5
        await SeedTerminalAgentAsync(teamB, runB, model: "claude-opus-4-8", input: 2_000_000, output: 0);   // $10

        var rollupA = await ComputeRollupAsync(teamA);
        rollupA.EstimatedCostUsd.ShouldBe(5m, "team A sees ONLY its own $5 — never team B's $10");
        rollupA.TotalInputTokens.ShouldBe(1_000_000);
        rollupA.Runs.ShouldContain(r => r.WorkflowRunId == runA);
        rollupA.Runs.ShouldNotContain(r => r.WorkflowRunId == runB, "team B's run must NEVER enter team A's rollup (tenancy fail-closed)");

        var rollupB = await ComputeRollupAsync(teamB);
        rollupB.EstimatedCostUsd.ShouldBe(10m);
        rollupB.Runs.ShouldNotContain(r => r.WorkflowRunId == runA);
    }

    [Fact]
    public async Task ComputeRunAsync_summarizes_a_single_runs_agents()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = Guid.NewGuid();

        await SeedTerminalAgentAsync(teamId, runId, model: "claude-opus-4-8", input: 200_000, output: 100_000);   // 0.2*5 + 0.1*25 = $3.50
        await SeedTerminalAgentAsync(teamId, runId, model: "claude-opus-4-8", input: 0, output: 0);                // $0 (priced, free)
        // A different run's agent must not bleed into this run's summary.
        await SeedTerminalAgentAsync(teamId, Guid.NewGuid(), model: "claude-opus-4-8", input: 9_000_000, output: 0);

        using var scope = _fixture.BeginScope();
        var summary = await scope.Resolve<ITeamCostService>().ComputeRunAsync(teamId, runId, CancellationToken.None);

        summary.WorkflowRunId.ShouldBe(runId);
        summary.CountedRuns.ShouldBe(2, "only the two agents of THIS run");
        summary.SummedInputTokens.ShouldBe(200_000);
        summary.SummedOutputTokens.ShouldBe(100_000);
        summary.EstimatedCostUsd.ShouldBe(3.5m);
        summary.UnknownCostRuns.ShouldBe(0, "both agents are priceable opus runs");
    }

    [Fact]
    public async Task The_team_scoped_query_handler_returns_only_the_callers_team_spend()
    {
        // The production read path: GetTeamCostRollupQuery resolves the team from ICurrentTeam (the X-Team-Id header),
        // NEVER the wire — so a caller only ever bills its own team. This is the controller's exact dispatch.
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        await SeedTerminalAgentAsync(teamA, Guid.NewGuid(), model: "claude-opus-4-8", input: 1_000_000, output: 0);   // $5
        await SeedTerminalAgentAsync(teamB, Guid.NewGuid(), model: "claude-opus-4-8", input: 9_000_000, output: 0);   // $45 — must not leak

        using var scope = _fixture.BeginScopeAs(userA, teamA, Roles.Admin);
        var rollup = await scope.Resolve<IMediator>().Send(new GetTeamCostRollupQuery { Since = null });

        rollup.EstimatedCostUsd.ShouldBe(5m, "the handler scopes to the caller's team via ICurrentTeam — team B's $45 is invisible");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<TeamCostRollup> ComputeRollupAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ITeamCostService>().ComputeRollupAsync(teamId, since: null, CancellationToken.None);
    }

    private Task SeedTerminalAgentAsync(Guid teamId, Guid runId, string model, int input, int output) =>
        SeedAgentAsync(teamId, runId, model, AgentRunStatus.Succeeded, ResultJson(input, output));

    private Task SeedRunningAgentAsync(Guid teamId, Guid runId, string model) =>
        SeedAgentAsync(teamId, runId, model, AgentRunStatus.Running, resultJson: null);

    private async Task SeedAgentAsync(Guid teamId, Guid runId, string model, AgentRunStatus status, string? resultJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = runId,
            Harness = "claude-code",
            Status = status,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "g", Harness = "claude-code", Model = model }, AgentJson.Options),
            ResultJson = resultJson,
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
