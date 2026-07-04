using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Journal.Describers;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the describer registry — the GENERICITY core of the journal. Pins the load-bearing guarantee: EVERY timeline
/// event becomes exactly one step and NONE is ever silently dropped. A known source dispatches to its describer's
/// journal step-kind; an UNKNOWN source (a future timeline source, a new kind) still renders via the mandatory fallback,
/// carrying its own title + tone. Dispatch is first-claimant; with no specific describer the fallback is the floor. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class JournalStepDescriberRegistryTests
{
    private static readonly IJournalStepDescriberRegistry Registry = new JournalStepDescriberRegistry(
        new IJournalStepDescriber[] { new SupervisorStepDescriber(), new ToolStepDescriber(), new AgentEventStepDescriber(), new LifecycleStepDescriber() },
        new FallbackStepDescriber());

    private static RunTimelineEvent Event(string sourceKey, string kind = "k", string title = "t", string? summary = null,
        TimelineSeverity sev = TimelineSeverity.Info, TimelineLevel level = TimelineLevel.Detail, string? agentRunId = null, string? nodeId = null) => new()
    {
        Id = "id-1", Kind = kind, Title = title, Summary = summary, Severity = sev, Level = level,
        OccurredAt = DateTimeOffset.UtcNow, Order = 0, SourceKey = sourceKey, AgentRunId = agentRunId, NodeId = nodeId,
    };

    [Theory]
    [InlineData("supervisor", JournalStepKinds.Decision)]
    [InlineData("tool-calls", JournalStepKinds.Tool)]
    [InlineData("agent-events", JournalStepKinds.Agent)]
    [InlineData("run-record", JournalStepKinds.Lifecycle)]
    public void Dispatches_each_source_to_its_journal_step_kind(string sourceKey, string expectedKind)
    {
        Registry.Describe(Event(sourceKey)).Kind.ShouldBe(expectedKind);
    }

    [Theory]
    // A run-record model-call (interaction.*) reads as a distinct model_call step; run/node lifecycle stays lifecycle.
    [InlineData("interaction.completed", JournalStepKinds.ModelCall)]
    [InlineData("interaction.failed", JournalStepKinds.ModelCall)]
    [InlineData("run.started", JournalStepKinds.Lifecycle)]
    [InlineData("node.completed", JournalStepKinds.Lifecycle)]
    public void A_run_record_interaction_reads_as_a_model_call_the_rest_lifecycle(string recordKind, string expectedKind)
    {
        Registry.Describe(Event("run-record", kind: recordKind)).Kind.ShouldBe(expectedKind);
    }

    [Theory]
    // An agent's reasoning block reads as a distinct thinking step (the folded chain-of-thought); its other narrative
    // events (a file edit, a test result, its final summary) stay a plain agent step.
    [InlineData("agent.Reasoning", JournalStepKinds.Thinking)]
    [InlineData("agent.FileChanged", JournalStepKinds.Agent)]
    [InlineData("agent.FinalSummary", JournalStepKinds.Agent)]
    public void An_agent_reasoning_reads_as_a_thinking_step_the_rest_agent(string agentKind, string expectedKind)
    {
        Registry.Describe(Event("agent-events", kind: agentKind)).Kind.ShouldBe(expectedKind);
    }

    [Fact]
    public void An_unknown_source_still_becomes_a_step_via_the_fallback()
    {
        // THE genericity guarantee: a future source / kind no describer claims is NEVER dropped — it degrades to a
        // legible generic step, carrying its own title + tone.
        var step = Registry.Describe(Event("some-future-source-2027", kind: "future.kind", title: "a new beat", sev: TimelineSeverity.Warning));

        step.Kind.ShouldBe(JournalStepKinds.Event, "an unclaimed event degrades to a generic step — never dropped");
        step.Title.ShouldBe("a new beat", "the fallback preserves the event's own title so it still reads");
        step.Tone.ShouldBe(TimelineSeverity.Warning, "and its tone");
    }

    [Fact]
    public void Carries_the_common_fields_off_the_event()
    {
        var step = Registry.Describe(Event("supervisor", title: "Supervisor planned the work", summary: "detail line",
            sev: TimelineSeverity.Success, level: TimelineLevel.Milestone, agentRunId: "a1", nodeId: "sup"));

        step.Id.ShouldBe("id-1");
        step.Title.ShouldBe("Supervisor planned the work");
        step.Detail.ShouldBe("detail line");
        step.Tone.ShouldBe(TimelineSeverity.Success);
        step.Milestone.ShouldBeTrue("a milestone-level event is a milestone step");
        step.AgentRunId.ShouldBe("a1");
        step.NodeId.ShouldBe("sup");
    }

    [Fact]
    public void A_detail_level_event_is_not_a_milestone()
    {
        Registry.Describe(Event("tool-calls", level: TimelineLevel.Detail)).Milestone.ShouldBeFalse();
    }

    [Theory]
    [InlineData("supervisor.plan", "plan")]
    [InlineData("supervisor.spawn", "spawn")]
    [InlineData("supervisor.ask_human", "ask_human")]
    [InlineData("supervisor.merge", "merge")]
    public void A_supervisor_decision_carries_its_verb_for_the_semantic_pill(string timelineKind, string expectedVerb)
    {
        // The verb rides off the timeline kind ("supervisor.<DecisionKind>") so the frontend renders a PLAN/DISPATCH/ASK
        // pill under one "Supervisor" actor lane, instead of tagging every beat a generic "decision". The DecisionKind is
        // the LOWERCASE/snake_case constant the ledger stores (SupervisorDecisionKinds), NOT PascalCase.
        Registry.Describe(Event("supervisor", kind: timelineKind)).Verb.ShouldBe(expectedVerb);
    }

    [Fact]
    public void A_non_decision_step_has_no_verb()
    {
        Registry.Describe(Event("tool-calls", kind: "tool.call")).Verb.ShouldBeNull("only a supervisor decision carries a verb pill");
    }

    [Fact]
    public void With_no_specific_describer_every_event_falls_to_the_fallback()
    {
        var floor = new JournalStepDescriberRegistry(Array.Empty<IJournalStepDescriber>(), new FallbackStepDescriber());

        floor.Describe(Event("supervisor")).Kind.ShouldBe(JournalStepKinds.Event, "the fallback is the floor — even a would-be-known source renders when its describer is absent");
    }

    [Fact]
    public void Dispatch_is_first_claimant()
    {
        // Two describers that both claim: the FIRST registered wins (deterministic dispatch, not last / ambiguous).
        var registry = new JournalStepDescriberRegistry(
            new IJournalStepDescriber[] { new AlwaysDescriber("first"), new AlwaysDescriber("second") },
            new FallbackStepDescriber());

        registry.Describe(Event("anything")).Kind.ShouldBe("first", "the first claimant wins");
    }

    [Fact]
    public void The_fallback_is_only_used_when_no_describer_claims()
    {
        var registry = new JournalStepDescriberRegistry(new IJournalStepDescriber[] { new AlwaysDescriber("claimed") }, new FallbackStepDescriber());

        registry.Describe(Event("anything")).Kind.ShouldBe("claimed", "a claimed event never reaches the fallback");
    }

    /// <summary>A test describer that claims every event and stamps a given kind — for dispatch-order + fallback-precedence tests.</summary>
    private sealed class AlwaysDescriber : IJournalStepDescriber
    {
        private readonly string _kind;
        public AlwaysDescriber(string kind) => _kind = kind;
        public bool CanDescribe(RunTimelineEvent e) => true;
        public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, _kind);
    }
}
