using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="AgentMetricsReader"/> from DI): the per-agent metrics a plain
/// agent.code / map agent surfaces — proving duration (off the persisted timestamps), tokens (off the real
/// <c>ResultJson</c>), model (off <c>TaskJson</c>), and the side-effecting tool count (off <c>tool_call_ledger</c>,
/// EXCLUDING the <c>decision.request</c> HITL envelopes) all read back team-scoped from the durable rows. A
/// foreign-team agent is never returned.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentMetricsReaderFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentMetricsReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public async Task Reads_duration_tokens_model_and_the_side_effecting_tool_count_team_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t0 = DateTimeOffset.UtcNow;
        var agentId = await SeedAgentRunAsync(teamId, AgentRunStatus.Succeeded, t0.AddSeconds(-40), t0.AddSeconds(-12),   // a 28s run
            taskJson: Task("claude-opus-4"), resultJson: Result(300, 120));

        // Two side-effecting tool calls + one decision.request envelope (a HITL ask, which must NOT count).
        await SeedToolAsync(teamId, agentId, "git.open_change_set");
        await SeedToolAsync(teamId, agentId, "agent.run_command");
        await SeedToolAsync(teamId, agentId, DecisionToolKinds.DecisionRequest);

        IReadOnlyDictionary<Guid, AgentRunMetrics> metrics;
        using (var scope = _fixture.BeginScope())
            metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, new[] { agentId }, DateTimeOffset.UtcNow, CancellationToken.None);

        var m = metrics[agentId];
        m.Status.ShouldBe(AgentRunStatus.Succeeded);
        m.DurationMs.ShouldNotBeNull();
        m.DurationMs!.Value.ShouldBeInRange(27_500L, 28_500L);   // the final span off the persisted timestamps
        m.InputTokens.ShouldBe(300);
        m.OutputTokens.ShouldBe(120);
        m.Model.ShouldBe("claude-opus-4");
        m.ToolCount.ShouldBe(2, "the decision.request HITL envelope is excluded from the side-effecting tool count");
    }

    [Fact]
    public async Task Does_not_return_a_foreign_team_agent()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var foreign = await SeedAgentRunAsync(otherTeamId, AgentRunStatus.Running, DateTimeOffset.UtcNow.AddSeconds(-5), completedAt: null, Task(null), resultJson: null);

        using var scope = _fixture.BeginScope();
        var metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, new[] { foreign }, DateTimeOffset.UtcNow, CancellationToken.None);

        metrics.ShouldNotContainKey(foreign, "the reader is team-scoped — another team's agent row is invisible");
    }

    [Fact]
    public async Task An_in_flight_agent_has_a_live_growing_duration_and_no_tokens_yet()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // Running: StartedAt set, no CompletedAt, no ResultJson yet (the harness hasn't reported usage).
        var agentId = await SeedAgentRunAsync(teamId, AgentRunStatus.Running, DateTimeOffset.UtcNow.AddSeconds(-6), completedAt: null, Task("claude-opus-4"), resultJson: null);

        using var scope = _fixture.BeginScope();
        var metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, new[] { agentId }, DateTimeOffset.UtcNow, CancellationToken.None);

        var m = metrics[agentId];
        m.Status.ShouldBe(AgentRunStatus.Running);
        m.DurationMs.ShouldNotBeNull();
        m.DurationMs!.Value.ShouldBeGreaterThanOrEqualTo(6_000, "live elapsed (now − StartedAt) while still running, not null");
        m.InputTokens.ShouldBeNull("no result blob yet → tokens unknown");
        m.OutputTokens.ShouldBeNull();
        m.Model.ShouldBe("claude-opus-4", "the model is known from the task envelope even before the result lands");
        m.ToolCount.ShouldBe(0, "no side-effecting tool calls yet");
    }

    private static string Result(int input, int output) =>
        JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output } }, AgentJson.Options);

    private static string Task(string? model) =>
        JsonSerializer.Serialize(new AgentTask { Goal = "g", Harness = "claude-code", Model = model }, AgentJson.Options);

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, AgentRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string taskJson, string? resultJson)
    {
        var id = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentRun.Add(new AgentRun
        {
            Id = id, TeamId = teamId, Harness = "claude-code", Status = status, TaskJson = taskJson, ResultJson = resultJson,
            StartedAt = startedAt, CompletedAt = completedAt,
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();

        return id;
    }

    private async Task SeedToolAsync(Guid teamId, Guid agentRunId, string toolKind)
    {
        var id = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = id, TeamId = teamId, AgentRunId = agentRunId, ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{id:N}", InputHash = ZeroHash, Status = ToolCallLedgerStatus.Succeeded,
            CreatedBy = Guid.Empty, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }
}
