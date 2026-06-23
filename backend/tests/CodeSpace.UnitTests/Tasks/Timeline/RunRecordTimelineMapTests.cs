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
    private static WorkflowRunRecord Record(string recordType, string? nodeId = null, string payloadJson = "{}", long sequence = 1) => new()
    {
        Id = Guid.NewGuid(),
        RunId = Guid.NewGuid(),
        Sequence = sequence,
        RecordType = recordType,
        NodeId = nodeId,
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
}
