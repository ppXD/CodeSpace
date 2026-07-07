using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="AgentMetricsReader"/> from DI): the per-agent metrics a plain
/// agent.code / map agent surfaces — proving duration (off the persisted timestamps), tokens (off the real
/// <c>ResultJson</c>), model (off <c>TaskJson</c>), and the actual tool count (off the <c>agent_run_event</c> log's
/// <see cref="AgentEventKind.ToolCall"/> entries — the agent's real tool calls, not the governed ledger) all read back
/// team-scoped from the durable rows. A foreign-team agent is never returned.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentMetricsReaderFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentMetricsReaderFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Reads_duration_tokens_model_and_the_actual_tool_count_team_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t0 = DateTimeOffset.UtcNow;
        var agentId = await SeedAgentRunAsync(teamId, AgentRunStatus.Succeeded, t0.AddSeconds(-40), t0.AddSeconds(-12),   // a 28s run
            taskJson: Task("claude-opus-4"), resultJson: Result(300, 120));

        // The agent's ACTUAL (harness-native) tool calls, off the event log — two tool calls + one non-tool event
        // (reasoning) that must NOT count. A governed ledger row is NO longer what drives the count.
        await SeedEventAsync(agentId, AgentEventKind.ToolCall, "WebSearch");
        await SeedEventAsync(agentId, AgentEventKind.ToolCall, "Read");
        await SeedEventAsync(agentId, AgentEventKind.Reasoning, "thinking about the plan");

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
        m.Goal.ShouldBe("g", "the agent's goal reads back off the real TaskJson as its display name");
        m.ToolCount.ShouldBe(2, "the count is the agent's actual ToolCall events; a non-tool (reasoning) event is excluded");
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
        m.CostUsd.ShouldBeNull("no tokens yet → no cost");
        m.FilesChanged.ShouldBeNull("no result blob yet → unknown file count");
    }

    [Fact]
    public async Task Reads_cost_from_the_priced_model_and_the_changed_file_count()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var t0 = DateTimeOffset.UtcNow;
        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed",
            TokenUsage = new AgentTokenUsage { InputTokens = 1_000_000, OutputTokens = 1_000_000 },
            ChangedFiles = new[] { "src/a.cs", "src/b.cs", "README.md" },
        }, AgentJson.Options);

        var agentId = await SeedAgentRunAsync(teamId, AgentRunStatus.Succeeded, t0.AddSeconds(-10), t0, Task("claude-opus-4-8"), resultJson);

        using var scope = _fixture.BeginScope();
        var metrics = await scope.Resolve<AgentMetricsReader>().ReadAsync(teamId, new[] { agentId }, DateTimeOffset.UtcNow, CancellationToken.None);

        var m = metrics[agentId];
        m.CostUsd.ShouldBe(30m, "claude-opus-4-8 is priced $5/$25 per M → 1M in + 1M out = $30, computed once in the reader");
        m.FilesChanged.ShouldBe(3, "the git-truth changed-file count off the persisted result");
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

    private async Task SeedEventAsync(Guid agentRunId, AgentEventKind kind, string text)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = agentRunId, Kind = kind, Text = text });
        await db.SaveChangesAsync();
    }
}
