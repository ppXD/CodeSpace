using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The pure agent-event → timeline mapping: the narrative kind set is exactly the story-line kinds (file edits,
/// tests, errors, warnings, the final summary) and excludes the verbose chatter; the severity follows the kind; a
/// long final summary is clamped; an empty text falls back to the kind name; the agent + node are stamped on. No DB.
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

    [Fact]
    public void The_narrative_set_excludes_every_verbose_or_elsewhere_handled_kind()
    {
        // Pin out EVERY non-narrative kind (the chatter that belongs in Trace, the lifecycle handled by the
        // run-record source, and the approval/decision kinds handled by the decision queue) — so a future kind
        // accidentally slipping into the story line is caught.
        var excluded = new[]
        {
            AgentEventKind.Queued, AgentEventKind.Started, AgentEventKind.AssistantMessage, AgentEventKind.Reasoning,
            AgentEventKind.PlanUpdate, AgentEventKind.ToolCall, AgentEventKind.CommandExecuted,
            AgentEventKind.ApprovalRequested, AgentEventKind.ApprovalResolved, AgentEventKind.Completed,
        };

        foreach (var kind in excluded)
            AgentEventTimelineMap.Narrative.ShouldNotContain(kind, $"{kind} is not part of the run story line");
    }

    [Theory]
    [InlineData(AgentEventKind.FileChanged, TimelineSeverity.Info)]
    [InlineData(AgentEventKind.TestOutput, TimelineSeverity.Info)]
    [InlineData(AgentEventKind.FinalSummary, TimelineSeverity.Info)]
    [InlineData(AgentEventKind.Warning, TimelineSeverity.Warning)]
    [InlineData(AgentEventKind.Error, TimelineSeverity.Error)]
    public void Severity_follows_the_kind(AgentEventKind kind, TimelineSeverity expected)
    {
        var ev = AgentEventTimelineMap.ToEvent(Event(kind), NodeByAgent);

        ev.Severity.ShouldBe(expected);
        ev.Kind.ShouldBe($"agent.{kind}");
        ev.SourceKey.ShouldBe(AgentEventTimelineMap.Key);
    }

    [Theory]
    // An agent's error + final summary are story milestones; its file edits / test output / warnings fold (the wave
    // and the agent's own terminal already carry them).
    [InlineData(AgentEventKind.Error, TimelineLevel.Milestone)]
    [InlineData(AgentEventKind.FinalSummary, TimelineLevel.Milestone)]
    [InlineData(AgentEventKind.FileChanged, TimelineLevel.Detail)]
    [InlineData(AgentEventKind.TestOutput, TimelineLevel.Detail)]
    [InlineData(AgentEventKind.Warning, TimelineLevel.Detail)]
    public void Levels_errors_and_the_final_summary_above_routine_edits(AgentEventKind kind, TimelineLevel expected)
    {
        AgentEventTimelineMap.ToEvent(Event(kind), NodeByAgent).Level.ShouldBe(expected);
    }

    [Fact]
    public void Stamps_the_agent_node_and_order()
    {
        var ev = AgentEventTimelineMap.ToEvent(Event(AgentEventKind.FileChanged, "edited auth/session.ts", sequence: 7), NodeByAgent);

        ev.Title.ShouldBe("edited auth/session.ts");
        ev.AgentRunId.ShouldBe(AgentId.ToString());
        ev.NodeId.ShouldBe("code");
        ev.Order.ShouldBe(7);
    }

    [Fact]
    public void Falls_back_to_a_null_node_when_the_agent_is_not_in_the_map()
    {
        var ev = AgentEventTimelineMap.ToEvent(Event(AgentEventKind.FileChanged, agentId: Guid.NewGuid()), NodeByAgent);

        ev.NodeId.ShouldBeNull();
    }

    [Fact]
    public void Clamps_a_long_final_summary_to_a_headline()
    {
        var huge = new string('a', 500);

        var ev = AgentEventTimelineMap.ToEvent(Event(AgentEventKind.FinalSummary, huge), NodeByAgent);

        ev.Title.Length.ShouldBeLessThanOrEqualTo(201);   // 200 chars + the ellipsis
        ev.Title.ShouldEndWith("…");
    }

    [Fact]
    public void Falls_back_to_the_kind_name_for_an_empty_text()
    {
        var ev = AgentEventTimelineMap.ToEvent(Event(AgentEventKind.FileChanged, text: "  "), NodeByAgent);

        ev.Title.ShouldBe("FileChanged");
    }

    [Fact]
    public void Clamps_without_splitting_a_surrogate_pair()
    {
        // 199 BMP chars, then an emoji (U+1F600 — a surrogate pair whose HIGH half lands at the would-be cut index
        // 199), then filler. A naive cut at 200 keeps the lone high surrogate; the clamp must back off.
        var text = new string('a', 199) + "\U0001F600" + new string('b', 100);

        var ev = AgentEventTimelineMap.ToEvent(Event(AgentEventKind.FinalSummary, text), NodeByAgent);

        ev.Title.ShouldEndWith("…");
        char.IsHighSurrogate(ev.Title[^2]).ShouldBeFalse("the clamp backed off the surrogate pair — no lone high surrogate before the ellipsis");
    }
}
