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
/// agent.run / map agent surfaces — proving duration (off the persisted timestamps), tokens (off the real
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

    [Fact]
    public async Task Workflow_observation_ignores_multi_megabyte_baggage_and_is_exact_team_run_scoped()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = Guid.NewGuid();
        var wrongWorkflowRunId = Guid.NewGuid();
        var changedFiles = Enumerable.Range(0, 55).Select(i => $"src/file-{i:D2}.cs").ToArray();
        var stats = changedFiles.Select((path, i) => new FileDiffStat(path, i, i + 1)).ToArray();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Failed,
            ExitReason = "failed",
            Summary = new string('s', 2 * 1024 * 1024),
            Error = new string('e', 450),
            Model = "claude-opus-4-8",
            TokenUsage = new AgentTokenUsage { InputTokens = 1_000, OutputTokens = 500 },
            ChangedFiles = changedFiles,
            FileStats = stats,
        }, AgentJson.Options);
        var taskJson = JsonSerializer.Serialize(new AgentTask
        {
            Goal = "ignored because displayTitle is present",
            DisplayTitle = new string('g', 160),
            SystemPrompt = new string('p', 2 * 1024 * 1024),
            Harness = "claude-code",
            Model = "task-model",
            ResumeFromSessionId = "session-1",
        }, AgentJson.Options);
        var target = await SeedAgentRunAsync(teamId, AgentRunStatus.Failed, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow, taskJson, resultJson, workflowRunId);
        var wrongRun = await SeedAgentRunAsync(teamId, AgentRunStatus.Succeeded, null, null, Task("wrong-run"), Result(1, 1), wrongWorkflowRunId);
        var standalone = await SeedAgentRunAsync(teamId, AgentRunStatus.Succeeded, null, null, Task("standalone"), Result(1, 1));
        var foreign = await SeedAgentRunAsync(foreignTeamId, AgentRunStatus.Succeeded, null, null, Task("foreign"), Result(1, 1), workflowRunId);

        IReadOnlyDictionary<Guid, AgentRunMetrics> metrics;
        using (var scope = _fixture.BeginScope())
            metrics = await scope.Resolve<AgentMetricsReader>().ReadForWorkflowRunAsync(teamId, workflowRunId, new[] { target, wrongRun, standalone, foreign }, DateTimeOffset.UtcNow, CancellationToken.None);

        metrics.Keys.ShouldBe(new[] { target });
        var m = metrics[target];
        m.Status.ShouldBe(AgentRunStatus.Failed);
        m.InputTokens.ShouldBe(1_000);
        m.OutputTokens.ShouldBe(500);
        m.Model.ShouldBe("claude-opus-4-8", "the reported model still wins over the task-pinned model");
        m.Harness.ShouldBe("claude-code");
        m.Resumed.ShouldBeTrue();
        m.Goal.ShouldBe(new string('g', 140) + "…");
        m.Error.ShouldBe(new string('e', 400) + "…");
        m.FilesChanged.ShouldBe(55, "the full count is computed in PostgreSQL without transferring the full array");
        m.ChangedFiles.ShouldBe(changedFiles.Take(40));
        m.ChangedFileStats.ShouldBe(stats.Take(40));
    }

    [Fact]
    public async Task Workflow_observation_reads_pascal_legacy_and_fails_closed_per_malformed_leaf()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = Guid.NewGuid();
        var legacyResult = JsonSerializer.Serialize(new
        {
            TokenUsage = new { InputTokens = 31, OutputTokens = 17 },
            Model = "legacy-model",
            ChangedFiles = new[] { "legacy/a.cs", "legacy/b.cs" },
            FileStats = new[] { new { Path = "legacy/a.cs", Additions = 3, Deletions = 1 } },
            Error = "\tlegacy failure\t",
        });
        var legacyTask = JsonSerializer.Serialize(new { Goal = "\tLegacy goal\t\r\nsecond line", Harness = "legacy-cli", ResumeFromSessionId = "legacy-session" });
        var legacy = await SeedAgentRunAsync(teamId, AgentRunStatus.Failed, null, null, legacyTask, legacyResult, workflowRunId);
        var malformed = await SeedAgentRunAsync(teamId, AgentRunStatus.Failed, null, null, "42", "[]", workflowRunId, rowError: "row fallback");
        var wrongTypes = await SeedAgentRunAsync(teamId, AgentRunStatus.Failed, null, null,
            JsonSerializer.Serialize(new { goal = 123, harness = new { bad = true }, model = "task-fallback" }),
            JsonSerializer.Serialize(new { tokenUsage = "unknown", model = new { bad = true }, changedFiles = new { bad = true }, fileStats = 5, error = false }),
            workflowRunId, rowError: "typed row fallback");
        var oversizedLabels = await SeedAgentRunAsync(teamId, AgentRunStatus.Running, null, null,
            JsonSerializer.Serialize(new { goal = "g", harness = new string('h', 513), model = new string('t', 513) }),
            JsonSerializer.Serialize(new { model = new string('m', 513), changedFiles = Array.Empty<string>(), fileStats = Array.Empty<object>() }),
            workflowRunId);

        IReadOnlyDictionary<Guid, AgentRunMetrics> metrics;
        using (var scope = _fixture.BeginScope())
            metrics = await scope.Resolve<AgentMetricsReader>().ReadForWorkflowRunAsync(teamId, workflowRunId, new[] { legacy, malformed, wrongTypes, oversizedLabels }, DateTimeOffset.UtcNow, CancellationToken.None);

        var old = metrics[legacy];
        old.InputTokens.ShouldBe(31);
        old.OutputTokens.ShouldBe(17);
        old.Model.ShouldBe("legacy-model");
        old.Goal.ShouldBe("Legacy goal");
        old.Harness.ShouldBe("legacy-cli");
        old.Resumed.ShouldBeTrue();
        old.FilesChanged.ShouldBe(2);
        old.ChangedFiles.ShouldBe(new[] { "legacy/a.cs", "legacy/b.cs" });
        old.ChangedFileStats.ShouldBe(new[] { new FileDiffStat("legacy/a.cs", 3, 1) });
        old.Error.ShouldBe("legacy failure");

        metrics[malformed].InputTokens.ShouldBeNull();
        metrics[malformed].FilesChanged.ShouldBeNull();
        metrics[malformed].Goal.ShouldBeNull();
        metrics[malformed].Error.ShouldBe("row fallback");

        metrics[wrongTypes].InputTokens.ShouldBeNull();
        metrics[wrongTypes].FilesChanged.ShouldBeNull();
        metrics[wrongTypes].ChangedFiles.ShouldBeEmpty();
        metrics[wrongTypes].ChangedFileStats.ShouldBeEmpty();
        metrics[wrongTypes].Goal.ShouldBeNull();
        metrics[wrongTypes].Harness.ShouldBeNull();
        metrics[wrongTypes].Model.ShouldBe("task-fallback", "one malformed result leaf cannot erase a healthy task fallback");
        metrics[wrongTypes].Error.ShouldBe("typed row fallback");

        metrics[oversizedLabels].Model.ShouldBeNull("an oversized provider-controlled label fails closed instead of crossing the observation boundary");
        metrics[oversizedLabels].Harness.ShouldBeNull();
    }

    private static string Result(int input, int output) =>
        JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output } }, AgentJson.Options);

    private static string Task(string? model) =>
        JsonSerializer.Serialize(new AgentTask { Goal = "g", Harness = "claude-code", Model = model }, AgentJson.Options);

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, AgentRunStatus status, DateTimeOffset? startedAt, DateTimeOffset? completedAt, string taskJson, string? resultJson, Guid? workflowRunId = null, string? rowError = null)
    {
        var id = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentRun.Add(new AgentRun
        {
            Id = id, TeamId = teamId, WorkflowRunId = workflowRunId, Harness = "claude-code", Status = status, TaskJson = taskJson, ResultJson = resultJson, Error = rowError,
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
