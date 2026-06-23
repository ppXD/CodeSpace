using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The pure supervisor-decision → timeline mapping: the title names the supervisor as the actor per verb (an OPEN
/// verb degrades, never drops); severity rides the CLOSED status axis; the spawn headline counts the staged agents;
/// the ask_human summary surfaces the question (+ answer once folded) while other verbs carry the failure reason. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionTimelineMapTests
{
    private static SupervisorDecisionRecord Decision(string kind, SupervisorDecisionStatus status = SupervisorDecisionStatus.Succeeded, long sequence = 1, string? outcome = null, string? error = null) => new()
    {
        Id = Guid.NewGuid(),
        SupervisorRunId = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = kind,
        Status = status,
        PayloadJson = "{}",
        OutcomeJson = outcome,
        Error = error,
        CreatedDate = DateTimeOffset.UtcNow,
    };

    private static string StagedAgents(int count) =>
        JsonSerializer.Serialize(new { agentRunIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString()).ToArray() });

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, "Supervisor planned the work")]
    [InlineData(SupervisorDecisionKinds.Retry, "Supervisor retried a subtask")]
    [InlineData(SupervisorDecisionKinds.AskHuman, "Supervisor asked you")]
    [InlineData(SupervisorDecisionKinds.Merge, "Supervisor merged the results")]
    [InlineData(SupervisorDecisionKinds.Resolve, "Supervisor resolved a conflict")]
    [InlineData(SupervisorDecisionKinds.Stop, "Supervisor stopped")]
    public void Title_names_the_supervisor_per_verb(string kind, string expected)
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(kind)).Title.ShouldBe(expected);
    }

    [Fact]
    public void An_unknown_open_verb_degrades_to_a_generic_title_rather_than_dropping()
    {
        var ev = SupervisorDecisionTimelineMap.ToEvent(Decision("teleport"));

        ev.Title.ShouldBe("Supervisor: teleport");
        ev.Kind.ShouldBe("supervisor.teleport", "the open verb still rides through on Kind for the Trace tab");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    [InlineData("teleport")]
    public void Every_decision_is_a_milestone_never_folds(string kind)
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(kind)).Level.ShouldBe(TimelineLevel.Milestone, "a supervisor decision is always a story beat");
    }

    [Theory]
    [InlineData(0, "Supervisor spawned agents")]   // a still-pending spawn has staged none yet
    [InlineData(1, "Supervisor spawned 1 agent")]
    [InlineData(3, "Supervisor spawned 3 agents")]
    public void Spawn_title_counts_the_staged_agents(int count, string expected)
    {
        var outcome = count == 0 ? null : StagedAgents(count);

        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Spawn, outcome: outcome)).Title.ShouldBe(expected);
    }

    [Theory]
    [InlineData(SupervisorDecisionStatus.Succeeded, TimelineSeverity.Success)]
    [InlineData(SupervisorDecisionStatus.Failed, TimelineSeverity.Error)]
    [InlineData(SupervisorDecisionStatus.Expired, TimelineSeverity.Warning)]
    [InlineData(SupervisorDecisionStatus.Pending, TimelineSeverity.Info)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, TimelineSeverity.Info)]
    [InlineData(SupervisorDecisionStatus.Running, TimelineSeverity.Info)]
    public void Severity_rides_the_status_axis(SupervisorDecisionStatus status, TimelineSeverity expected)
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Plan, status)).Severity.ShouldBe(expected);
    }

    [Fact]
    public void Ask_human_summary_is_the_question_until_the_answer_is_folded_in()
    {
        var asked = Decision(SupervisorDecisionKinds.AskHuman, outcome: SupervisorOutcome.FoldAnswer("Deploy to prod?", "tok", answer: null));
        SupervisorDecisionTimelineMap.ToEvent(asked).Summary.ShouldBe("Deploy to prod?");

        var answered = Decision(SupervisorDecisionKinds.AskHuman, outcome: SupervisorOutcome.FoldAnswer("Deploy to prod?", "tok", "yes, ship it"));
        SupervisorDecisionTimelineMap.ToEvent(answered).Summary.ShouldBe("Deploy to prod? — yes, ship it");
    }

    [Fact]
    public void Ask_human_with_no_recorded_question_falls_back_to_the_error()
    {
        // ask_human degraded (e.g. "not supported until E4") — no question in the outcome, so the failure reason carries.
        var ev = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.AskHuman, SupervisorDecisionStatus.Failed, error: "ask_human not supported yet"));

        ev.Summary.ShouldBe("ask_human not supported yet");
    }

    [Fact]
    public void A_non_ask_human_verb_summary_is_its_error_or_null()
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Merge, SupervisorDecisionStatus.Failed, error: "merge conflict")).Summary.ShouldBe("merge conflict");
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Merge)).Summary.ShouldBeNull("a clean success carries no failure reason");
    }

    [Fact]
    public void Stamps_a_stable_id_kind_order_time_and_source_with_no_node_or_agent_tag()
    {
        var d = Decision(SupervisorDecisionKinds.Spawn, sequence: 12, outcome: StagedAgents(2));

        var ev = SupervisorDecisionTimelineMap.ToEvent(d);

        ev.Id.ShouldBe($"supervisor-{d.Id:N}");
        ev.Kind.ShouldBe("supervisor.spawn");
        ev.Order.ShouldBe(12, "the per-run Sequence is the same-tick tie-break");
        ev.OccurredAt.ShouldBe(d.CreatedDate);
        ev.SourceKey.ShouldBe(SupervisorDecisionTimelineMap.Key);
        ev.NodeId.ShouldBeNull("a supervisor decision is not tied to one node");
        ev.AgentRunId.ShouldBeNull("a spawn fans out to many agents — no single agent tag");
    }
}
