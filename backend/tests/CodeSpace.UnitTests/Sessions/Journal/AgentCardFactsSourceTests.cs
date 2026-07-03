using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the agent-card mapping — the shared metrics projection → a journal card. Pins the ground-truth passthrough
/// (status · model · files · duration · cost), the token total (input + output, but NULL when the agent reported no usage
/// so it never reads as a measured zero), and the neutral label fallback when the task named no goal. The source's tape
/// walk + batched read is integration-tested (it needs the real metrics reader over Postgres); this pins the pure map.
/// </summary>
[Trait("Category", "Unit")]
public class AgentCardFactsSourceTests
{
    private static AgentRunMetrics Metrics(string? goal = "Build the login form", AgentRunStatus status = AgentRunStatus.Succeeded,
        int? inTok = 1200, int? outTok = 340, int tools = 6, string? model = "claude-opus-4-8", decimal? cost = 0.42m, long? durationMs = 45000, int? files = 3) =>
        new()
        {
            Status = status, Goal = goal, DurationMs = durationMs, InputTokens = inTok, OutputTokens = outTok,
            ToolCount = tools, Model = model, CostUsd = cost, FilesChanged = files,
            ChangedFileStats = new[] { new FileDiffStat("auth/session.ts", 42, 3) },
        };

    [Fact]
    public void Maps_the_ground_truth_metrics_onto_the_card()
    {
        var id = Guid.NewGuid();

        var card = AgentCardFactsSource.ToCard(id, Metrics());

        card.AgentRunId.ShouldBe(id);
        card.Label.ShouldBe("Build the login form");
        card.Status.ShouldBe(AgentRunStatus.Succeeded);
        card.Model.ShouldBe("claude-opus-4-8");
        card.DurationMs.ShouldBe(45000);
        card.Tokens.ShouldBe(1540, "input + output");
        card.ToolCount.ShouldBe(6);
        card.CostUsd.ShouldBe(0.42m);
        card.FilesChanged.ShouldBe(3);
        card.Files.Select(f => (f.Path, f.Additions, f.Deletions)).ShouldBe(new[] { ("auth/session.ts", (int?)42, (int?)3) }, "the per-file diffstat rides onto the card");
    }

    [Fact]
    public void Tokens_is_null_when_the_agent_reported_no_usage()
    {
        // Both halves null → null total, NOT 0 — a card must not claim a measured zero when usage is simply unknown.
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(inTok: null, outTok: null)).Tokens
            .ShouldBeNull("no reported usage is unknown, not zero");
    }

    [Fact]
    public void Tokens_sums_a_one_sided_usage()
    {
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(inTok: 900, outTok: null)).Tokens.ShouldBe(900, "a present half + a null half is that half, not null");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_a_neutral_label_when_the_task_named_no_goal(string? goal)
    {
        AgentCardFactsSource.ToCard(Guid.NewGuid(), Metrics(goal: goal)).Label.ShouldBe("Agent", "an unnamed subtask still renders a card");
    }
}
