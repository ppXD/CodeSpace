using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The pure agent-event → timeline mapping: the narrative kind set is exactly the story-line kinds (reasoning, file
/// edits, tests, errors, warnings, the final summary) and excludes the remaining chatter; reasoning rides folded; an
/// error/warning follows the kind while the FINAL SUMMARY tone rides the agent's terminal status (a failed agent's
/// conclusion isn't a green success); a long final summary is clamped; a payload-less event falls back to a human
/// headline (never the raw enum); the agent + node are stamped on. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class AgentEventTimelineMapTests
{
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, string?> NodeByAgent = new Dictionary<Guid, string?> { [AgentId] = "code" };

    private static AgentRunEvent Event(AgentEventKind kind, string text = "x", long sequence = 1, Guid? agentId = null) => new()
    {
        Id = Guid.NewGuid(),
        AgentRunId = agentId ?? AgentId,
        Sequence = sequence,
        Kind = kind,
        Text = text,
        OccurredAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Project the event with its agent at <paramref name="status"/> (default in-flight Running → neutral Info for a FinalSummary, matching a not-yet-terminal agent).</summary>
    private static RunTimelineEvent To(AgentRunEvent e, AgentRunStatus status = AgentRunStatus.Running) =>
        AgentEventTimelineMap.ToEvent(e, NodeByAgent, new Dictionary<Guid, AgentRunStatus> { [e.AgentRunId] = status });

    [Fact]
    public void The_narrative_set_excludes_every_verbose_or_elsewhere_handled_kind()
    {
        var excluded = new[]
        {
            AgentEventKind.Queued, AgentEventKind.Started, AgentEventKind.AssistantMessage,
            AgentEventKind.PlanUpdate, AgentEventKind.ToolCall, AgentEventKind.CommandExecuted,
            AgentEventKind.ApprovalRequested, AgentEventKind.ApprovalResolved, AgentEventKind.Completed,
        };

        foreach (var kind in excluded)
            AgentEventTimelineMap.Narrative.ShouldNotContain(kind, $"{kind} is not part of the run story line");
    }

    [Fact]
    public void Reasoning_is_a_folded_story_beat_on_the_spine()
    {
        AgentEventTimelineMap.Narrative.ShouldContain(AgentEventKind.Reasoning, "reasoning surfaces as a folded story beat");

        var ev = To(Event(AgentEventKind.Reasoning, "the race is two concurrent 401s…"));
        ev.Level.ShouldBe(TimelineLevel.Detail, "reasoning folds away on the story line, like a file edit");
        ev.Severity.ShouldBe(TimelineSeverity.Info);
        ev.Kind.ShouldBe(AgentEventTimelineMap.ReasoningKind, "the emitted kind is the one the journal describer classifies as thinking");
    }

    [Theory]
    [InlineData(AgentEventKind.FileChanged, TimelineSeverity.Info)]
    [InlineData(AgentEventKind.TestOutput, TimelineSeverity.Info)]
    [InlineData(AgentEventKind.FinalSummary, TimelineSeverity.Info)]   // in-flight (Running) agent → neutral until terminal
    [InlineData(AgentEventKind.Warning, TimelineSeverity.Warning)]
    [InlineData(AgentEventKind.Error, TimelineSeverity.Error)]
    public void Severity_follows_the_kind(AgentEventKind kind, TimelineSeverity expected)
    {
        var ev = To(Event(kind));

        ev.Severity.ShouldBe(expected);
        ev.Kind.ShouldBe($"agent.{kind}");
        ev.SourceKey.ShouldBe(AgentEventTimelineMap.Key);
    }

    [Theory]
    // The FINAL SUMMARY — the conclusion the operator reads — takes its tone from the agent's TERMINAL status, so a
    // failed / timed-out / cancelled agent's closing beat doesn't read as a neutral success (and can't drift from its card).
    [InlineData(AgentRunStatus.Succeeded, TimelineSeverity.Success)]
    [InlineData(AgentRunStatus.Failed, TimelineSeverity.Error)]
    [InlineData(AgentRunStatus.TimedOut, TimelineSeverity.Error)]
    [InlineData(AgentRunStatus.Cancelled, TimelineSeverity.Error)]
    [InlineData(AgentRunStatus.NeedsReview, TimelineSeverity.Warning)]
    [InlineData(AgentRunStatus.Running, TimelineSeverity.Info)]
    public void Final_summary_tone_rides_the_agent_terminal_status(AgentRunStatus status, TimelineSeverity expected)
    {
        To(Event(AgentEventKind.FinalSummary), status).Severity.ShouldBe(expected);
    }

    [Fact]
    public void Final_summary_tone_is_neutral_when_the_agent_status_is_unknown()
    {
        // The agent isn't in the status map (a defensive gap) → the conclusion reads neutral Info, never a false success.
        var e = Event(AgentEventKind.FinalSummary);
        var ev = AgentEventTimelineMap.ToEvent(e, NodeByAgent, new Dictionary<Guid, AgentRunStatus>());

        ev.Severity.ShouldBe(TimelineSeverity.Info);
    }

    [Theory]
    [InlineData(AgentEventKind.Error, TimelineLevel.Milestone)]
    [InlineData(AgentEventKind.FinalSummary, TimelineLevel.Milestone)]
    [InlineData(AgentEventKind.FileChanged, TimelineLevel.Detail)]
    [InlineData(AgentEventKind.TestOutput, TimelineLevel.Detail)]
    [InlineData(AgentEventKind.Warning, TimelineLevel.Detail)]
    public void Levels_errors_and_the_final_summary_above_routine_edits(AgentEventKind kind, TimelineLevel expected)
    {
        To(Event(kind)).Level.ShouldBe(expected);
    }

    [Fact]
    public void Stamps_the_agent_node_and_order()
    {
        var ev = To(Event(AgentEventKind.FileChanged, "edited auth/session.ts", sequence: 7));

        ev.Title.ShouldBe("edited auth/session.ts");
        ev.AgentRunId.ShouldBe(AgentId.ToString());
        ev.NodeId.ShouldBe("code");
        ev.Order.ShouldBe(7);
    }

    [Fact]
    public void Falls_back_to_a_null_node_when_the_agent_is_not_in_the_map()
    {
        To(Event(AgentEventKind.FileChanged, agentId: Guid.NewGuid())).NodeId.ShouldBeNull();
    }

    [Fact]
    public void Clamps_a_long_final_summary_to_a_headline()
    {
        var ev = To(Event(AgentEventKind.FinalSummary, new string('a', 500)));

        ev.Title.Length.ShouldBeLessThanOrEqualTo(201);   // 200 chars + the ellipsis
        ev.Title.ShouldEndWith("…");
    }

    [Theory]
    // A payload-less event surfaces a HUMAN headline per kind, never the raw enum token (a harness that emits an empty
    // error/reasoning item must not read "Error"/"FinalSummary" as the story-line milestone).
    [InlineData(AgentEventKind.Error, "The agent hit an error")]
    [InlineData(AgentEventKind.Warning, "The agent flagged a warning")]
    [InlineData(AgentEventKind.FinalSummary, "The agent finished")]
    [InlineData(AgentEventKind.TestOutput, "The agent ran tests")]
    [InlineData(AgentEventKind.FileChanged, "The agent edited a file")]
    [InlineData(AgentEventKind.Reasoning, "The agent reasoned")]
    public void Falls_back_to_a_human_headline_for_an_empty_text(AgentEventKind kind, string expected)
    {
        To(Event(kind, text: "  ")).Title.ShouldBe(expected);
    }

    [Fact]
    public void Clamps_without_splitting_a_surrogate_pair()
    {
        var text = new string('a', 199) + "\U0001F600" + new string('b', 100);

        var ev = To(Event(AgentEventKind.FinalSummary, text));

        ev.Title.ShouldEndWith("…");
        char.IsHighSurrogate(ev.Title[^2]).ShouldBeFalse("the clamp backed off the surrogate pair — no lone high surrogate before the ellipsis");
    }
}
