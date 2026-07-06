using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The pure supervisor-decision → timeline mapping. Every verb's title, summary AND tone are OUTCOME-AWARE (read off
/// the recorded outcome): the plan names its subtask count, a spawn that staged nothing says so, a merge distinguishes
/// clean vs conflicted, a resolve verified vs needs-review, and a stop distinguishes a genuine success vs a model
/// give-up vs a server-forced bound. Severity rides the CLOSED status axis but a SUCCEEDED decision with a degraded
/// outcome reads amber, not green. An OPEN verb degrades, never drops. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDecisionTimelineMapTests
{
    private static SupervisorDecisionRecord Decision(string kind, SupervisorDecisionStatus status = SupervisorDecisionStatus.Succeeded, long sequence = 1, string? outcome = null, string? payload = null, string? error = null) => new()
    {
        Id = Guid.NewGuid(),
        SupervisorRunId = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = kind,
        Status = status,
        PayloadJson = payload ?? "{}",
        OutcomeJson = outcome,
        Error = error,
        CreatedDate = DateTimeOffset.UtcNow,
    };

    private static string StagedAgents(int count) =>
        JsonSerializer.Serialize(new { agentRunIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString()).ToArray() });

    private static string PlanPayload(int subtasks) =>
        JsonSerializer.Serialize(new { subtasks = Enumerable.Range(0, subtasks).Select(i => new { id = $"s{i}", title = $"Subtask {i}", instruction = "do it" }).ToArray() });

    private static string Integration(string status, params string[] conflictedFiles) =>
        JsonSerializer.Serialize(new { integration = new { status, outcomes = new[] { new { conflictedFiles } } } });

    private static string Resolution(bool verified) =>
        JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = Guid.NewGuid(), status = verified ? "Succeeded" : "Failed", summary = verified ? $"reconciled — {SupervisorResolverRecipe.TestsPassedMarker}" : "build failed" } } });

    private static string StopOutcome(string? outcome, string? summary) =>
        JsonSerializer.Serialize(new { stopped = true, outcome, summary });

    private static string ForcedPayload(string reason) => JsonSerializer.Serialize(new { reason });

    // ── Titles: one bare / in-flight shape per verb still reads sensibly ─────────────────────────────────────────

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan, "Supervisor planned the work")]
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

    // ── Plan: title names the authored subtask count ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "Supervisor planned the work")]   // pre-plan / unreadable payload → the bare verb
    [InlineData(1, "Supervisor planned 1 subtask")]
    [InlineData(4, "Supervisor planned 4 subtasks")]
    public void Plan_title_counts_the_authored_subtasks(int count, string expected)
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Plan, payload: PlanPayload(count))).Title.ShouldBe(expected);
    }

    // ── Spawn: title + summary reflect the fan-out width, incl. the empty-spawn no-op ───────────────────────────

    [Theory]
    [InlineData(1, "Supervisor spawned 1 agent")]
    [InlineData(3, "Supervisor spawned 3 agents")]
    public void Spawn_title_counts_the_staged_agents(int count, string expected)
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Spawn, outcome: StagedAgents(count))).Title.ShouldBe(expected);
    }

    [Fact]
    public void Spawn_that_settled_with_no_agent_says_it_dispatched_nothing()
    {
        // A SETTLED spawn that staged 0 agents dispatched nothing — the reported gap (a bland "spawned agents" implied
        // work started). Say "spawned no agents" + explain, so the empty-spawn beat is legible.
        var settled = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Succeeded, outcome: StagedAgents(0)));
        settled.Title.ShouldBe("Supervisor spawned no agents");
        settled.Summary.ShouldBe("No agent was dispatched this round — the supervisor staged no subtask to run.");

        // A still-PENDING spawn hasn't staged yet — keep the bare verb (it may still stage).
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Spawn, SupervisorDecisionStatus.Running, outcome: null)).Title.ShouldBe("Supervisor spawned agents");
    }

    [Fact]
    public void Retry_title_says_nothing_re_ran_when_a_settled_retry_staged_no_agent()
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Retry, outcome: StagedAgents(1))).Title
            .ShouldBe("Supervisor retried a subtask");

        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Retry, SupervisorDecisionStatus.Succeeded, outcome: StagedAgents(0))).Title
            .ShouldBe("Supervisor reviewed the results — no retry needed");
    }

    // ── Merge: clean vs conflicted vs failed ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_title_and_summary_reflect_the_integration_outcome()
    {
        var clean = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Merge, outcome: Integration("Clean")));
        clean.Title.ShouldBe("Supervisor merged the results");
        clean.Summary.ShouldBeNull("a clean integration carries no failure detail");
        clean.Severity.ShouldBe(TimelineSeverity.Success);

        var conflicted = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Merge, outcome: Integration("Conflicted", "a.cs", "b.cs")));
        conflicted.Title.ShouldBe("Supervisor hit a merge conflict in 2 files");
        conflicted.Summary.ShouldBe("Conflicted while integrating: a.cs, b.cs");
        conflicted.Severity.ShouldBe(TimelineSeverity.Warning, "a succeeded merge that conflicted reads amber, not a green win");

        var oneFile = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Merge, outcome: Integration("Conflicted", "only.cs")));
        oneFile.Title.ShouldBe("Supervisor hit a merge conflict in 1 file");
    }

    // ── Resolve: verified vs needs-review ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_title_and_tone_reflect_the_build_test_verdict()
    {
        var verified = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Resolve, outcome: Resolution(verified: true)));
        verified.Title.ShouldBe("Supervisor resolved the conflict");
        verified.Severity.ShouldBe(TimelineSeverity.Success);

        var unverified = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Resolve, outcome: Resolution(verified: false)));
        unverified.Title.ShouldBe("Supervisor's resolution needs review");
        unverified.Summary.ShouldBe("The reconciliation wasn't verified — the build or tests didn't pass on the resolved result.");
        unverified.Severity.ShouldBe(TimelineSeverity.Warning, "an unverified resolution is not a green win");
    }

    // ── Stop: genuine success vs model give-up vs server-forced bound ───────────────────────────────────────────

    [Fact]
    public void Stop_success_reads_neutral_and_green_with_the_closing_summary()
    {
        var ev = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Stop, outcome: StopOutcome("completed", "Shipped the fix and opened the PR.")));

        ev.Title.ShouldBe("Supervisor stopped");
        ev.Summary.ShouldBe("Shipped the fix and opened the PR.");
        ev.Severity.ShouldBe(TimelineSeverity.Success, "a genuine success stays green");
    }

    [Fact]
    public void Stop_model_give_up_reads_stopped_early_and_amber()
    {
        var ev = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Stop, outcome: StopOutcome(SupervisorStopPayload.NonConformantOutcome, "The model returned a malformed decision.")));

        ev.Title.ShouldBe("Supervisor stopped early");
        ev.Summary.ShouldBe("The model returned a malformed decision.");
        ev.Severity.ShouldBe(TimelineSeverity.Warning, "a fail-closed give-up is not a green success");
    }

    [Fact]
    public void Stop_server_forced_names_the_bound_and_reads_amber()
    {
        // The reported gap: a budget/governance/bound-forced stop stamps {reason} on the PAYLOAD (no outcome), so it
        // otherwise rendered "Supervisor stopped" + green + no reason. Now it names the bound + degrades.
        var ev = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Stop, payload: ForcedPayload(SupervisorStopReasons.BudgetExhausted), outcome: StopOutcome(null, null)));

        ev.Title.ShouldBe("Supervisor stopped — budget exhausted");
        ev.Summary.ShouldBe("budget exhausted", "the forced stop never renders a blank summary");
        ev.Severity.ShouldBe(TimelineSeverity.Warning, "a forced stop did not finish the work — it is not a green success");
    }

    // ── Severity: rides status, downgrades a succeeded-but-degraded decision to amber ───────────────────────────

    [Theory]
    [InlineData(SupervisorDecisionStatus.Succeeded, TimelineSeverity.Success)]
    [InlineData(SupervisorDecisionStatus.Failed, TimelineSeverity.Error)]
    [InlineData(SupervisorDecisionStatus.Expired, TimelineSeverity.Warning)]
    [InlineData(SupervisorDecisionStatus.Pending, TimelineSeverity.Info)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, TimelineSeverity.Info)]
    [InlineData(SupervisorDecisionStatus.Running, TimelineSeverity.Info)]
    public void Severity_rides_the_status_axis_for_a_verb_with_no_degraded_outcome(SupervisorDecisionStatus status, TimelineSeverity expected)
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Plan, status)).Severity.ShouldBe(expected);
    }

    // ── ask_human summary: question, then the answer once folded ────────────────────────────────────────────────

    [Fact]
    public void Ask_human_title_reflects_the_human_response()
    {
        // A parked ask (no answer yet) → the plain verb.
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.AskHuman, outcome: SupervisorOutcome.FoldAnswer("Deploy?", "tok", answer: null))).Title
            .ShouldBe("Supervisor asked you");

        // Answered → the title says so (the full Q&A rides the summary).
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.AskHuman, outcome: SupervisorOutcome.FoldAnswer("Deploy?", "tok", "yes"))).Title
            .ShouldBe("Supervisor asked you — answered");

        // Swept unanswered (the reaper expired it) → the title says so; its Expired status already tones it Warning.
        var expired = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.AskHuman, SupervisorDecisionStatus.Expired, outcome: SupervisorOutcome.FoldAnswer("Deploy?", "tok", answer: null)));
        expired.Title.ShouldBe("Supervisor's question went unanswered");
        expired.Severity.ShouldBe(TimelineSeverity.Warning);
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
        var ev = SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.AskHuman, SupervisorDecisionStatus.Failed, error: "ask_human not supported yet"));

        ev.Summary.ShouldBe("ask_human not supported yet");
    }

    [Fact]
    public void A_failed_verb_with_no_outcome_detail_falls_back_to_its_error()
    {
        SupervisorDecisionTimelineMap.ToEvent(Decision(SupervisorDecisionKinds.Merge, SupervisorDecisionStatus.Failed, error: "git exploded")).Summary.ShouldBe("git exploded");
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
