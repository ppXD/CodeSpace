using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

[Trait("Category", "Unit")]
public sealed class RoomLogSummaryTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    [Fact]
    public void Completed_v3_stream_with_manifest_is_integrity_verified()
    {
        var summary = RoomProjector.SummarizeLogs([Row(AgentRunLogStreamState.Completed, schemaVersion: 3, hasManifestDigest: true)]);

        summary.ShouldBe(new RoomAgentLogSummary(RoomAgentLogStatus.Verified, 1, "1 stream · 1 integrity verified"));
    }

    [Fact]
    public void Completed_legacy_stream_is_captured_without_an_integrity_claim()
    {
        var summary = RoomProjector.SummarizeLogs([Row(AgentRunLogStreamState.Completed, schemaVersion: 2, hasManifestDigest: false)]);

        summary.ShouldBe(new RoomAgentLogSummary(RoomAgentLogStatus.Captured, 1, "1 stream · 1 captured; integrity proof unavailable"));
    }

    [Fact]
    public void Any_open_stream_keeps_the_agent_finalizing()
    {
        var summary = RoomProjector.SummarizeLogs([
            Row(AgentRunLogStreamState.Completed, schemaVersion: 3, hasManifestDigest: true),
            Row(AgentRunLogStreamState.Open, schemaVersion: 3, hasManifestDigest: false),
        ]);

        summary.ShouldBe(new RoomAgentLogSummary(RoomAgentLogStatus.Finalizing, 2, "2 streams · 1 finalizing · 1 integrity verified"));
    }

    [Fact]
    public void Failure_states_outrank_success_and_render_in_deterministic_severity_order()
    {
        var summary = RoomProjector.SummarizeLogs([
            Row(AgentRunLogStreamState.CaptureFailed),
            Row(AgentRunLogStreamState.Completed, schemaVersion: 3, hasManifestDigest: true),
            Row(AgentRunLogStreamState.Corrupt),
            Row(AgentRunLogStreamState.Truncated),
            Row(AgentRunLogStreamState.Unavailable),
        ]);

        summary.ShouldBe(new RoomAgentLogSummary(RoomAgentLogStatus.Incomplete, 5, "5 streams · 1 corrupt · 1 capture failed · 1 unavailable · 1 truncated · 1 integrity verified"));
    }

    [Fact]
    public void An_open_stream_whose_remote_is_refusing_segments_is_held_not_finalizing()
    {
        // "Finalizing" through a storage incident is the reading an operator acts on wrongly: it claims the stream is
        // wrapping up when its head is frozen and its bytes are queued in the sandbox spool. The held count has to be
        // the headline, or the one fact worth knowing is the one the Room never says.
        var summary = RoomProjector.SummarizeLogs([
            Row(AgentRunLogStreamState.Open, remoteStalled: true),
            Row(AgentRunLogStreamState.Open),
        ]);

        summary.ShouldBe(new RoomAgentLogSummary(RoomAgentLogStatus.Stalled, 2, "2 streams · 1 held; storage unavailable · 1 finalizing"));
    }

    [Fact]
    public void A_settled_stream_that_still_carries_its_marker_is_read_by_its_state_not_the_marker()
    {
        // The marker is not cleared on the way to a terminal state — it is the durable record of WHY a stream parked,
        // and a park needs it. So the fold has to read it only while the stream is Open, or the opposite lie shows up:
        // a stream that reached Completed with a proof over every byte reported as an ongoing storage incident. The
        // marker is set here deliberately; with it at its default this assertion passes on a fold that never reads it.
        var summary = RoomProjector.SummarizeLogs([Row(AgentRunLogStreamState.Completed, schemaVersion: 3, hasManifestDigest: true, remoteStalled: true)]);

        summary.ShouldBe(new RoomAgentLogSummary(RoomAgentLogStatus.Verified, 1, "1 stream · 1 integrity verified"));
    }

    [Fact]
    public void A_stream_a_newer_capture_session_reclaimed_is_finalizing_again()
    {
        // The shape a worker restart leaves behind: the next capture claim clears both stall columns (the producer
        // that was holding those bytes is the one being superseded, and nothing else would ever clear its marker), so
        // the row the Room folds is an Open stream with no marker. The reclaim itself is proven against the real guard
        // in AgentRunLogRemoteStallFlowTests, which is the only place a capture session exists.
        var summary = RoomProjector.SummarizeLogs([Row(AgentRunLogStreamState.Open, remoteStalled: false)]);

        summary.ShouldBe(new RoomAgentLogSummary(RoomAgentLogStatus.Finalizing, 1, "1 stream · 1 finalizing"));
    }

    [Fact]
    public void A_terminal_stream_still_outranks_a_held_one()
    {
        var summary = RoomProjector.SummarizeLogs([
            Row(AgentRunLogStreamState.Open, remoteStalled: true),
            Row(AgentRunLogStreamState.CaptureFailed),
        ]);

        summary.Status.ShouldBe(RoomAgentLogStatus.Incomplete, "a span that is already lost outranks one that is only waiting");
        summary.Detail.ShouldBe("2 streams · 1 capture failed · 1 held; storage unavailable");
    }

    private static RoomProjector.AgentLogRow Row(AgentRunLogStreamState state, int schemaVersion = 3, bool hasManifestDigest = false, bool remoteStalled = false) =>
        new(AgentId, state, schemaVersion, hasManifestDigest, remoteStalled);
}
