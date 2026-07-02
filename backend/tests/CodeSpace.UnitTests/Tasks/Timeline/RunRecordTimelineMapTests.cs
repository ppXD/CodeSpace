using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The pure record→event mapping: only NARRATIVE-worthy lifecycle records map (run + node lifecycle, retries);
/// Trace-level noise (log / scope / variables / external-call detail) drops to null; the severity follows the
/// outcome; and the payload detail (error / wait_kind / attempt) is folded into the summary. No database — pure logic.
/// </summary>
[Trait("Category", "Unit")]
public class RunRecordTimelineMapTests
{
    private static WorkflowRunRecord Record(string recordType, string? nodeId = null, string payloadJson = "{}", long sequence = 1, string iterationKey = "") => new()
    {
        Id = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        Sequence = sequence,
        RecordType = recordType,
        NodeId = nodeId,
        IterationKey = iterationKey,
        OccurredAt = DateTimeOffset.UtcNow,
        PayloadJson = payloadJson,
    };

    [Theory]
    [InlineData(WorkflowRunRecordTypes.RunStarted, TimelineSeverity.Info)]
    [InlineData(WorkflowRunRecordTypes.RunCompleted, TimelineSeverity.Success)]
    [InlineData(WorkflowRunRecordTypes.RunFailed, TimelineSeverity.Error)]
    [InlineData(WorkflowRunRecordTypes.NodeStarted, TimelineSeverity.Info)]
    [InlineData(WorkflowRunRecordTypes.NodeCompleted, TimelineSeverity.Success)]
    [InlineData(WorkflowRunRecordTypes.NodeFailed, TimelineSeverity.Error)]
    [InlineData(WorkflowRunRecordTypes.NodeSuspended, TimelineSeverity.Warning)]
    public void Maps_a_lifecycle_record_to_an_event_with_the_outcome_severity(string recordType, TimelineSeverity expected)
    {
        var ev = RunRecordTimelineMap.ToEvent(Record(recordType, nodeId: "code")).ShouldNotBeNull();

        ev.Severity.ShouldBe(expected);
        ev.Kind.ShouldBe(recordType);
        ev.SourceKey.ShouldBe(RunRecordTimelineMap.Key);
        ev.Id.ShouldBe("record-1");
    }

    [Theory]
    [InlineData(WorkflowRunRecordTypes.Log)]
    [InlineData(WorkflowRunRecordTypes.ScopeResolved)]
    [InlineData(WorkflowRunRecordTypes.VariablesSnapshotted)]
    [InlineData(WorkflowRunRecordTypes.ReleaseLoaded)]
    [InlineData(WorkflowRunRecordTypes.ExternalCallStarted)]
    [InlineData(WorkflowRunRecordTypes.InteractionStarted)]   // the model-call OPEN bracket adds no outcome — Trace has it; the completed/failed record carries the narrative
    public void Drops_trace_level_noise_to_null(string recordType)
    {
        RunRecordTimelineMap.ToEvent(Record(recordType)).ShouldBeNull("Trace-level records are the audit line, not the human narrative");
    }

    [Fact]
    public void Folds_the_failure_error_into_the_summary()
    {
        var ev = RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.NodeFailed, nodeId: "code", payloadJson: """{"error":"tests failed"}""")).ShouldNotBeNull();

        ev.Title.ShouldBe("code failed");
        ev.Summary.ShouldBe("tests failed");
        ev.NodeId.ShouldBe("code");
    }

    [Fact]
    public void Builds_an_attempt_summary_for_a_retry()
    {
        var ev = RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.AttemptFailed, nodeId: "code", payloadJson: """{"attempt":1,"max_attempts":3,"error":"flaky"}""")).ShouldNotBeNull();

        ev.Title.ShouldBe("code retry");
        ev.Summary.ShouldBe("attempt 1/3: flaky");
        ev.Severity.ShouldBe(TimelineSeverity.Warning);
    }

    [Fact]
    public void A_run_level_record_has_no_node_id()
    {
        var ev = RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.RunStarted)).ShouldNotBeNull();

        ev.Title.ShouldBe("Run started");
        ev.NodeId.ShouldBeNull();
    }

    [Theory]
    // Run lifecycle, a node FAILURE, and a retry are milestones; the per-node started/completed/waiting/skipped churn
    // folds. RunReplayed is a resume mechanic (always Detail); a RESUME's repeated RunStarted is demoted by Project.
    [InlineData(WorkflowRunRecordTypes.RunStarted, TimelineLevel.Milestone)]
    [InlineData(WorkflowRunRecordTypes.RunCompleted, TimelineLevel.Milestone)]
    [InlineData(WorkflowRunRecordTypes.RunFailed, TimelineLevel.Milestone)]
    [InlineData(WorkflowRunRecordTypes.NodeFailed, TimelineLevel.Milestone)]
    [InlineData(WorkflowRunRecordTypes.AttemptFailed, TimelineLevel.Milestone)]
    [InlineData(WorkflowRunRecordTypes.RunReplayed, TimelineLevel.Detail)]
    [InlineData(WorkflowRunRecordTypes.NodeStarted, TimelineLevel.Detail)]
    [InlineData(WorkflowRunRecordTypes.NodeCompleted, TimelineLevel.Detail)]
    [InlineData(WorkflowRunRecordTypes.NodeSuspended, TimelineLevel.Detail)]
    [InlineData(WorkflowRunRecordTypes.NodeSkipped, TimelineLevel.Detail)]
    [InlineData(WorkflowRunRecordTypes.InteractionCompleted, TimelineLevel.Detail)]   // a model call is per-call cost detail, not a story beat — folded like node churn
    public void Levels_milestones_above_structural_node_churn(string recordType, TimelineLevel expected)
    {
        RunRecordTimelineMap.ToEvent(Record(recordType, nodeId: "code")).ShouldNotBeNull().Level.ShouldBe(expected);
    }

    [Fact]
    public void A_completed_model_call_folds_its_kind_model_and_token_cost_into_a_detail_event()
    {
        var payload = """{"kind":"agent.critic","provider":"anthropic","model":"claude-opus-4-8","usage":{"inputTokens":17,"outputTokens":19,"finishReason":"stop"}}""";

        var ev = RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.InteractionCompleted, nodeId: "review", payloadJson: payload)).ShouldNotBeNull();

        ev.Title.ShouldBe("Model call");
        ev.Severity.ShouldBe(TimelineSeverity.Info);
        ev.Level.ShouldBe(TimelineLevel.Detail, "per-call cost is folded detail, not a milestone");
        ev.NodeId.ShouldBe("review");
        ev.Summary.ShouldBe("agent.critic · claude-opus-4-8 · 36 tokens", "the kind + model + total token cost — the attribution the timeline previously dropped");
    }

    [Fact]
    public void A_completed_model_call_with_no_usage_omits_the_token_clause_rather_than_showing_zero()
    {
        var ev = RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.InteractionCompleted, nodeId: "gen", payloadJson: """{"kind":"llm.complete","model":"metis-coder"}""")).ShouldNotBeNull();

        ev.Summary.ShouldBe("llm.complete · metis-coder", "an unpriced / usage-less completion adds no '0 tokens' noise — the absent field is simply omitted");
    }

    [Fact]
    public void A_failed_model_call_is_a_detail_error_carrying_the_kind_and_error()
    {
        var ev = RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.InteractionFailed, nodeId: "gen", payloadJson: """{"kind":"llm.complete","provider":"anthropic","error":"gateway timed out"}""")).ShouldNotBeNull();

        ev.Title.ShouldBe("Model call failed");
        ev.Severity.ShouldBe(TimelineSeverity.Error);
        ev.Level.ShouldBe(TimelineLevel.Detail);
        ev.Summary.ShouldBe("llm.complete: gateway timed out");
    }

    [Fact]
    public void Project_keeps_only_the_first_RunStarted_a_milestone_and_folds_every_resume_into_detail()
    {
        // The engine writes RunStarted on every dispatch + RunReplayed on every resume; a supervisor suspends/resumes
        // once per turn, so these repeat. Only the FIRST RunStarted is a milestone — the rest, and all RunReplayed,
        // fold so the story isn't drowned by the resume mechanic.
        var records = new[]
        {
            Record(WorkflowRunRecordTypes.RunStarted, sequence: 1),                                  // first dispatch — the milestone
            Record(WorkflowRunRecordTypes.NodeSuspended, nodeId: "sup", sequence: 2),                // turn 1 parks
            Record(WorkflowRunRecordTypes.RunStarted, sequence: 3),                                  // resume re-dispatch — DETAIL
            Record(WorkflowRunRecordTypes.RunReplayed, sequence: 4),                                 // resume replay — DETAIL
            Record(WorkflowRunRecordTypes.NodeStarted, nodeId: "sup", sequence: 5),                  // sup re-enters — DETAIL
            Record(WorkflowRunRecordTypes.RunFailed, payloadJson: """{"error":"gateway timed out"}""", sequence: 6),
        };

        var events = RunRecordTimelineMap.Project(records);

        var levelByTitle = events.ToLookup(e => e.Title);
        levelByTitle["Run started"].Select(e => e.Level).ShouldBe(new[] { TimelineLevel.Milestone, TimelineLevel.Detail }, "first RunStarted milestone, the resume RunStarted folds");
        levelByTitle["Run replayed"].ShouldHaveSingleItem().Level.ShouldBe(TimelineLevel.Detail, "a replay is always a resume mechanic");
        levelByTitle["Run failed"].ShouldHaveSingleItem().Level.ShouldBe(TimelineLevel.Milestone, "the real outcome stays a milestone");
    }

    [Fact]
    public void A_fanned_out_branch_failure_folds_to_detail_while_the_container_and_top_level_failures_stay_milestones()
    {
        // A 12-branch map that fails every branch must NOT flood the narrative with 12 "agent failed" milestones — a
        // branch failure (non-empty iteration key) is folded DETAIL; its own agent terminal carries the error.
        RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.NodeFailed, nodeId: "agent", iterationKey: "map#3", payloadJson: """{"error":"boom"}"""))
            .ShouldNotBeNull().Level.ShouldBe(TimelineLevel.Detail);

        // A nested map-in-map branch (composed key) folds too.
        RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.NodeFailed, nodeId: "agent", iterationKey: "outer#0/inner#2"))
            .ShouldNotBeNull().Level.ShouldBe(TimelineLevel.Detail);

        // The map CONTAINER's own failure (empty key) and a plain top-level node failure stay the story's milestones.
        RunRecordTimelineMap.ToEvent(Record(WorkflowRunRecordTypes.NodeFailed, nodeId: "map"))
            .ShouldNotBeNull().Level.ShouldBe(TimelineLevel.Milestone);
    }
}
