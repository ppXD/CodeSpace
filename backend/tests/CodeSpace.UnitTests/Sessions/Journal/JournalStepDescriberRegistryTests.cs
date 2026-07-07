using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Journal.Describers;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
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
        new IJournalStepDescriber[] { new SupervisorStepDescriber(), new MapPlannerStepDescriber(), new MapDispatchStepDescriber(), new ToolStepDescriber(), new AgentEventStepDescriber(), new ReviewVerdictStepDescriber(), new LifecycleStepDescriber() },
        new FallbackStepDescriber());

    private static RunTimelineEvent Event(string sourceKey, string kind = "k", string title = "t", string? summary = null,
        TimelineSeverity sev = TimelineSeverity.Info, TimelineLevel level = TimelineLevel.Detail, string? agentRunId = null, string? nodeId = null, string? iterationKey = null) => new()
    {
        Id = "id-1", Kind = kind, Title = title, Summary = summary, Severity = sev, Level = level,
        OccurredAt = DateTimeOffset.UtcNow, Order = 0, SourceKey = sourceKey, AgentRunId = agentRunId, NodeId = nodeId, IterationKey = iterationKey,
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

    // ── The adversarial exchange: review / revise classification (E) ─────────

    [Fact]
    public void The_synthetic_verdict_event_is_THE_review_beat_for_every_harness()
    {
        // The verdict beat rides the SYNTHETIC review.verdict event (read off the reviewer's durable result), NOT the
        // harness event log — codex-cli emits no final-summary event at all, which once hid a real run's verdict.
        var step = Registry.Describe(Event(ReviewVerdictTimelineMap.Key, kind: ReviewVerdictTimelineMap.VerdictKind,
            title: "Independent reviewer flagged the plan — 2 issues", summary: "the plan schedules finished work",
            sev: TimelineSeverity.Warning, level: TimelineLevel.Milestone, iterationKey: "#plan-review"));

        step.Kind.ShouldBe(JournalStepKinds.Review);
        step.Beat.ShouldBeTrue("the reviewer's verdict is a first-class orchestration beat — the adversarial exchange shows in the ③ timeline");
        step.Verb.ShouldBe("review", "the frontend's REVIEW pill");
        step.Milestone.ShouldBeTrue();
        step.Title.ShouldBe("Independent reviewer flagged the plan — 2 issues");
        step.Detail.ShouldBeNull("the verdict card (facts) carries the rationale — not a duplicated detail line");
        step.Tone.ShouldBe(TimelineSeverity.Warning, "a flagged verdict reads warm, an approval green — the tone carries the outcome");
    }

    [Theory]
    [InlineData("map#0#review", "Independent reviewer inspected the produced work")]
    [InlineData("#plan-review", "Independent reviewer verified the plan against the repository")]
    [InlineData("boss#turn1#0#review", "Independent reviewer inspected the produced work")]
    public void A_reviewer_runs_final_summary_folds_as_review_background_with_a_human_title(string iterationKey, string expectedTitle)
    {
        // The raw final message is the VERDICT contract line ("Reviewed. VERDICT: {json}") — it must never leak, but it
        // is NOT the beat either (the synthetic review.verdict event is) — so it folds as reviewer background.
        var step = Registry.Describe(Event("agent-events", kind: "agent.FinalSummary", title: """Reviewed. VERDICT: {"approved": false}""", iterationKey: iterationKey));

        step.Kind.ShouldBe(JournalStepKinds.Review);
        step.Beat.ShouldBeFalse("the synthetic verdict event is the one beat — the harness's own final message folds");
        step.Milestone.ShouldBeFalse();
        step.Title.ShouldBe(expectedTitle, "the raw VERDICT contract line never leaks into the journal");
        step.Detail.ShouldBeNull();
    }

    [Fact]
    public void A_reviewer_runs_other_events_fold_as_review_background()
    {
        var step = Registry.Describe(Event("agent-events", kind: "agent.Warning", title: "cloning the produced branch", iterationKey: "map#0#review"));

        step.Kind.ShouldBe(JournalStepKinds.Review, "every reviewer event groups under the review kind");
        step.Beat.ShouldBeFalse("only the verdict is a beat — the reviewer's working chatter folds");
        step.Title.ShouldBe("cloning the produced branch", "a non-verdict event keeps its own words");
    }

    [Fact]
    public void A_producers_revise_announcement_is_the_REVISE_beat()
    {
        var step = Registry.Describe(Event("agent-events", kind: "agent.Warning",
            title: "Verification failed — revising (round 1 of 2). the reviewer flagged a placeholder hack", iterationKey: "map#0", sev: TimelineSeverity.Warning));

        step.Kind.ShouldBe(JournalStepKinds.Revise);
        step.Beat.ShouldBeTrue("a revise round is a first-class beat — the exchange reads review → revise → review");
        step.Verb.ShouldBe("revise");
        step.Title.ShouldContain("round 1 of 2", customMessage: "the announcement keeps its round + reason");
        step.Tone.ShouldBe(TimelineSeverity.Warning);
    }

    [Fact]
    public void An_ordinary_agent_warning_is_not_a_revise_beat()
    {
        var step = Registry.Describe(Event("agent-events", kind: "agent.Warning", title: "low disk space", iterationKey: "map#0"));

        step.Kind.ShouldBe(JournalStepKinds.Agent, "only the pinned revise announcement classifies as a revise beat");
        step.Beat.ShouldBeFalse();
    }

    [Fact]
    public void A_producers_final_summary_with_no_iteration_key_stays_a_plain_agent_step() =>
        Registry.Describe(Event("agent-events", kind: "agent.FinalSummary", title: "done", iterationKey: null))
            .Kind.ShouldBe(JournalStepKinds.Agent, "a keyless run (a standalone agent) can never be mistaken for a reviewer");

    [Fact]
    public void A_model_critic_verdict_event_rides_the_same_review_grammar()
    {
        // H1: model-critic verdicts (folded on the decision tape) share the review-verdict provenance key, so the SAME
        // describer renders them — one grammar for every reviewer, model or agent.
        var step = Registry.Describe(Event(ReviewVerdictTimelineMap.Key, kind: ReviewVerdictTimelineMap.VerdictKind,
            title: "Model critic flagged the plan draft — 2 issues", sev: TimelineSeverity.Warning, level: TimelineLevel.Milestone));

        step.Kind.ShouldBe(JournalStepKinds.Review);
        step.Beat.ShouldBeTrue();
        step.Verb.ShouldBe("review");
    }

    [Fact]
    public void The_decision_review_map_pins_the_id_and_the_headline()
    {
        var id = Guid.NewGuid();

        Core.Services.Tasks.Timeline.Sources.DecisionReviewTimelineMap.EventId(id, 1).ShouldBe($"review-d{id:N}-1");

        Core.Services.Tasks.Timeline.Sources.DecisionReviewTimelineMap.TitleFor(new Messages.Agents.SupervisorDecisionReview { Approved = false, Rationale = "r", Issues = new[] { "a", "b" }, Scope = "plan", DraftAttribution = "plan draft" })
            .ShouldBe("Model critic flagged the plan draft — 2 issues", "WHO + outcome + WHAT (the discarded draft)");
        Core.Services.Tasks.Timeline.Sources.DecisionReviewTimelineMap.TitleFor(new Messages.Agents.SupervisorDecisionReview { Approved = true, Rationale = "r", Scope = "decision" })
            .ShouldBe("Model critic approved the decision");
        Core.Services.Tasks.Timeline.Sources.DecisionReviewTimelineMap.TitleFor(new Messages.Agents.SupervisorDecisionReview { Approved = false, Rationale = "r", Scope = "decision" })
            .ShouldBe("Model critic flagged the decision", "no draft ⇒ the decision itself was flagged (the escalation's second rung)");

        Core.Services.Tasks.Timeline.Sources.DecisionReviewTimelineMap.TitleFor(new Messages.Agents.SupervisorDecisionReview { Approved = true, Rationale = "r", Scope = "plan" }, index: 1)
            .ShouldBe("Model critic approved the revised plan", "a later rung reviews the REVISION — say so, or two same-titled beats read as a stutter");
        Core.Services.Tasks.Timeline.Sources.DecisionReviewTimelineMap.TitleFor(new Messages.Agents.SupervisorDecisionReview { Approved = false, Rationale = "r", Scope = "decision" }, index: 1)
            .ShouldBe("Model critic flagged the revised decision");
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
        var step = Registry.Describe(Event("supervisor", kind: timelineKind));

        step.Verb.ShouldBe(expectedVerb);
        step.Beat.ShouldBeTrue("a supervisor decision is an orchestration beat — it shows in the ③ timeline");
    }

    [Fact]
    public void A_flow_map_planner_is_a_beat_with_the_plan_verb()
    {
        // The same generic-beat seam: a NON-supervisor run's planner node becomes a plan beat, so its plan shows in the ③
        // timeline (a "Planned N subtasks" beat with its plan card) exactly like a supervisor PLAN — read-only, no frontend
        // change. The verb is "map_plan", DISTINCT from the supervisor's "plan": the frontend renders the same PLAN pill
        // for both, but only "plan" is supervisor-distinctive, so a pure map run's actor lane reads "Workflow" — the exact
        // parallel to map "dispatch" vs supervisor "spawn".
        var step = Registry.Describe(Event(MapPlannerTimelineMap.Key, kind: MapPlannerTimelineMap.PlanKind, title: "Planned 5 subtasks"));

        step.Beat.ShouldBeTrue("a map planner is an orchestration beat");
        step.Verb.ShouldBe("map_plan", "distinct from the supervisor's 'plan' so it isn't supervisor-distinctive for the actor lane");
        step.Title.ShouldBe("Planned 5 subtasks");
    }

    [Fact]
    public void A_flow_map_dispatch_is_a_beat_with_the_dispatch_verb()
    {
        // The generic-beat seam paying off: a NON-supervisor run's map fan-out becomes a "dispatch" beat, so it shows in
        // the ③ timeline with its cards — no frontend change, no kind==="decision" hardcode.
        var step = Registry.Describe(Event(MapDispatchTimelineMap.Key, kind: MapDispatchTimelineMap.DispatchKind, title: "Dispatched 5 agents"));

        step.Beat.ShouldBeTrue("a map dispatch is an orchestration beat");
        step.Verb.ShouldBe("dispatch");
        step.Title.ShouldBe("Dispatched 5 agents");
    }

    [Fact]
    public void A_non_decision_step_has_no_verb_and_is_not_a_beat()
    {
        var step = Registry.Describe(Event("tool-calls", kind: "tool.call"));

        step.Verb.ShouldBeNull("only an orchestration beat carries a verb pill");
        step.Beat.ShouldBeFalse("a tool call is background chatter — it folds, not a ③ beat");
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
