using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

public sealed class AgentRunEventDataWireTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, AgentRunEventDataWire.MaximumRangeBytes + 1)]
    [InlineData(long.MaxValue, 1)]
    public void Invalid_ranges_are_rejected_before_artifact_io(long offsetBytes, int limitBytes)
    {
        AgentRunEventDataWire.ValidRange(Query(offsetBytes, limitBytes)).ShouldBeFalse();
    }

    [Fact]
    public void Maximum_non_overflowing_range_is_admitted()
    {
        AgentRunEventDataWire.ValidRange(Query(long.MaxValue - AgentRunEventDataWire.MaximumRangeBytes,
            AgentRunEventDataWire.MaximumRangeBytes)).ShouldBeTrue();
    }

    [Fact]
    public void Available_range_preserves_identity_metadata_and_bounded_progress()
    {
        var query = Query(4, 4);
        var artifactId = Guid.NewGuid();
        var read = ArtifactRangeReadResult.Available([1, 2, 3, 4], 12, new string('a', 64), "application/json", false);

        var result = AgentRunEventDataWire.Available(query, artifactId, read);

        result.AgentRunId.ShouldBe(query.AgentRunId);
        result.EventSequence.ShouldBe(query.EventSequence);
        result.DataArtifactId.ShouldBe(artifactId);
        result.ReturnedBytes.ShouldBe(4);
        result.TotalBytes.ShouldBe(12);
        result.NextOffsetBytes.ShouldBe(8);
        result.Content.ShouldBe(new byte[] { 1, 2, 3, 4 });
        result.IntegrityVerified.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ArtifactRangeReadState.MetadataMissing, AgentRunEventDataReadAvailability.MetadataMissing, false)]
    [InlineData(ArtifactRangeReadState.PhysicalObjectMissing, AgentRunEventDataReadAvailability.PhysicalObjectMissing, false)]
    [InlineData(ArtifactRangeReadState.IntegrityFailure, AgentRunEventDataReadAvailability.IntegrityFailure, false)]
    [InlineData(ArtifactRangeReadState.BackendUnavailable, AgentRunEventDataReadAvailability.BackendUnavailable, true)]
    [InlineData(ArtifactRangeReadState.AccessDenied, AgentRunEventDataReadAvailability.AccessDenied, false)]
    [InlineData(ArtifactRangeReadState.InvalidOffset, AgentRunEventDataReadAvailability.InvalidRange, false)]
    public void Artifact_failures_remain_closed_typed_unavailable(ArtifactRangeReadState state,
        AgentRunEventDataReadAvailability availability, bool retryable)
    {
        var query = Query(0, 64);
        var read = ArtifactRangeReadResult.Failed(state, 100, new string('b', 64), "application/json");

        var result = AgentRunEventDataWire.Unavailable(query, Guid.NewGuid(), AgentRunEventDataWire.Availability(state), read);

        result.Availability.ShouldBe(availability);
        result.IsRetryable.ShouldBe(retryable);
        result.ReturnedBytes.ShouldBe(0);
        result.Content.ShouldBeEmpty();
        result.TotalBytes.ShouldBe(100);
    }

    private static ReadAgentRunEventDataRangeQuery Query(long offsetBytes, int limitBytes) => new()
    {
        AgentRunId = Guid.NewGuid(), EventSequence = 42, OffsetBytes = offsetBytes, LimitBytes = limitBytes,
    };
}
