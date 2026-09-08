using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Room;
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

    private static RoomProjector.AgentLogRow Row(AgentRunLogStreamState state, int schemaVersion = 3, bool hasManifestDigest = false) =>
        new(AgentId, state, schemaVersion, hasManifestDigest);
}
