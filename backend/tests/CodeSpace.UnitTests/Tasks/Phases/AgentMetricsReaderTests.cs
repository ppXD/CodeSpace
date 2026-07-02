using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>
/// The pure <see cref="AgentMetricsReader.Build"/> fold — the single place that turns an agent's persisted state
/// (timestamps + <c>ResultJson</c> + <c>TaskJson</c> + a tool count) into the <see cref="AgentRunMetrics"/> the run
/// detail surfaces. The team-scoped DB read is integration-tested; here we pin the duration arithmetic + the DEFENSIVE
/// deserialize (tokens null until the result lands, model null when blank, a malformed blob reads as unknown, never throws).
/// </summary>
[Trait("Category", "Unit")]
public class AgentMetricsReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    private static string Result(int input, int output) =>
        JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output } }, AgentJson.Options);

    private static string Task(string? model, string goal = "do the thing") =>
        JsonSerializer.Serialize(new AgentTask { Goal = goal, Harness = "claude-code", Model = model }, AgentJson.Options);

    [Fact]
    public void Projects_tokens_model_and_a_final_duration_from_the_persisted_blobs()
    {
        var started = Now.AddSeconds(-30);
        var completed = Now.AddSeconds(-8);   // a 22s run

        var m = AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Succeeded, started, completed, Result(120, 45), Task("claude-opus-4"), toolCount: 6, Now);

        m.Status.ShouldBe(AgentRunStatus.Succeeded);
        m.DurationMs.ShouldBe(22_000);
        m.InputTokens.ShouldBe(120);
        m.OutputTokens.ShouldBe(45);
        m.ToolCount.ShouldBe(6);
        m.Model.ShouldBe("claude-opus-4");
        m.Goal.ShouldBe("do the thing", "the agent's goal becomes its display name so a plain node/map agent isn't shown as a structural map#N key");
    }

    [Theory]
    [InlineData("Refactor the auth module", "Refactor the auth module")]                                  // inline goal → verbatim
    [InlineData("Implement the login endpoint\n\nUse OAuth2 with PKCE", "Implement the login endpoint")]  // B1: the goal is the CLEAN task → its FIRST block/line (the persona is no longer prepended)
    [InlineData("First line of the task\nsecond line", "First line of the task")]                          // multi-line → first line only
    [InlineData("   ", null)]                                                                               // blank goal → null, so the row keeps its structural fallback
    public void Names_the_agent_by_a_concise_title_from_its_goal(string goal, string? expected)
    {
        AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Running, Now.AddSeconds(-1), null, null, Task(null, goal), 0, Now)
            .Goal.ShouldBe(expected);
    }

    [Fact]
    public void A_very_long_goal_title_is_capped_with_an_ellipsis()
    {
        var m = AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Running, Now.AddSeconds(-1), null, null, Task(null, new string('x', 200)), 0, Now);

        m.Goal!.Length.ShouldBe(141);          // 140 chars + the … ellipsis
        m.Goal.ShouldEndWith("…");
    }

    [Fact]
    public void Duration_is_live_elapsed_while_running_and_null_before_start()
    {
        AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Running, Now.AddSeconds(-10), completedAt: null, resultJson: null, taskJson: Task(null), toolCount: 0, Now)
            .DurationMs.ShouldBe(10_000);   // live elapsed (now − started), not yet terminal

        AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Queued, startedAt: null, completedAt: null, resultJson: null, taskJson: null, toolCount: 0, Now)
            .DurationMs.ShouldBeNull();      // hasn't started
    }

    [Fact]
    public void Duration_clamps_a_negative_span_to_zero()
    {
        AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Succeeded, Now, Now.AddSeconds(-5), resultJson: null, taskJson: null, toolCount: 0, Now)
            .DurationMs.ShouldBe(0);   // completed before started (clock skew) → 0, never negative
    }

    [Fact]
    public void Tokens_and_model_stay_null_when_the_blobs_are_absent_or_blank()
    {
        var m = AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Running, Now.AddSeconds(-3), completedAt: null, resultJson: null, taskJson: Task("   "), toolCount: 0, Now);

        m.InputTokens.ShouldBeNull();    // no result yet → no tokens
        m.OutputTokens.ShouldBeNull();
        m.Model.ShouldBeNull();          // blank model → no chip
    }

    [Fact]
    public void A_malformed_blob_reads_as_unknown_never_throws()
    {
        var m = AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Failed, Now.AddSeconds(-2), Now, resultJson: "{ not json", taskJson: "also not json", toolCount: 1, Now);

        m.InputTokens.ShouldBeNull();
        m.Model.ShouldBeNull();
        m.ToolCount.ShouldBe(1);         // the non-JSON figures still project
        m.DurationMs.ShouldBe(2_000);
        m.CostUsd.ShouldBeNull();        // no model/tokens → no cost
        m.FilesChanged.ShouldBeNull();   // no result → unknown file count
    }

    [Fact]
    public void Prices_a_priced_model_and_counts_changed_files()
    {
        // claude-opus-4-8 is priced $5/$25 per M tokens (input/output). 1M in + 1M out → $5 + $25 = $30. Three files → 3.
        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed",
            TokenUsage = new AgentTokenUsage { InputTokens = 1_000_000, OutputTokens = 1_000_000 },
            ChangedFiles = new[] { "src/A.cs", "src/B.cs", "README.md" },
        }, AgentJson.Options);

        var m = AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Succeeded, Now.AddSeconds(-5), Now, resultJson, Task("claude-opus-4-8"), toolCount: 0, Now);

        m.CostUsd.ShouldBe(30m, "1M in × $5/M + 1M out × $25/M = $30 — computed once, server-side");
        m.FilesChanged.ShouldBe(3);
    }

    [Fact]
    public void Cost_is_null_for_an_unpriced_model_but_tokens_and_a_zero_file_count_still_project()
    {
        // An OpenAI/Codex model is intentionally absent from the price table → fail-open NULL cost (never a misleading 0).
        var m = AgentMetricsReader.Build(Guid.NewGuid(), AgentRunStatus.Succeeded, Now.AddSeconds(-5), Now, Result(500, 200), Task("gpt-5-codex"), toolCount: 0, Now);

        m.InputTokens.ShouldBe(500);
        m.CostUsd.ShouldBeNull("an unpriced model fails open to null cost, never 0");
        m.FilesChanged.ShouldBe(0, "a completed result with an empty changedFiles is a real 'touched none' (0), not unknown (null)");
    }
}
