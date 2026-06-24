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

    private static string Task(string? model) =>
        JsonSerializer.Serialize(new AgentTask { Goal = "do the thing", Harness = "claude-code", Model = model }, AgentJson.Options);

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
    }
}
