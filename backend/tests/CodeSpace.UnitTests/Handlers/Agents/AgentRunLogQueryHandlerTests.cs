using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

[Trait("Category", "Unit")]
public sealed class AgentRunLogQueryHandlerTests
{
    [Fact]
    public void Every_query_requires_team_membership()
    {
        new ListAgentRunLogsQuery { AgentRunId = Guid.NewGuid() }.ShouldBeAssignableTo<IRequireTeamMembership>();
        new GetAgentRunLogQuery { AgentRunId = Guid.NewGuid(), StreamId = Guid.NewGuid() }.ShouldBeAssignableTo<IRequireTeamMembership>();
        new ReadAgentRunLogRangeQuery { AgentRunId = Guid.NewGuid(), StreamId = Guid.NewGuid() }.ShouldBeAssignableTo<IRequireTeamMembership>();
    }

    [Fact]
    public void Cursor_is_stable_and_malformed_values_fail_loudly()
    {
        var expected = new AgentRunLogCursor(new DateTimeOffset(638908128000000000, TimeSpan.Zero), Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        AgentRunLogCursor.Decode(expected.Encode()).ShouldBe(expected);
        Should.Throw<InvalidOperationException>(() => AgentRunLogCursor.Decode("not-a-cursor"));
        AgentRunLogCursor.Decode(null).ShouldBeNull();
    }

    [Fact]
    public async Task Get_requires_the_stream_to_belong_to_the_route_agent_run()
    {
        var teamId = Guid.NewGuid();
        var routeRunId = Guid.NewGuid();
        var service = new StubLogService { Metadata = new AgentRunLogMetadataResult.Found(Metadata(Guid.NewGuid())) };
        var handler = new GetAgentRunLogQueryHandler(service, new StubCurrentTeam(teamId));

        var result = await handler.Handle(new GetAgentRunLogQuery { AgentRunId = routeRunId, StreamId = Guid.NewGuid() }, CancellationToken.None);

        result.ShouldBeNull();
        service.MetadataTeamId.ShouldBe(teamId);
    }

    [Fact]
    public async Task Read_returns_bounded_raw_content_metadata_and_clamps_the_requested_page()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var metadata = Metadata(runId) with { TotalBytes = 3_000_000 };
        var service = new StubLogService
        {
            Metadata = new AgentRunLogMetadataResult.Found(metadata),
            Range = new AgentRunLogRangeResult.Available(metadata, 17, [1, 2, 3]),
        };
        var handler = new ReadAgentRunLogRangeQueryHandler(service, new StubCurrentTeam(teamId));

        var result = await handler.Handle(new ReadAgentRunLogRangeQuery { AgentRunId = runId, StreamId = metadata.StreamId, OffsetBytes = 17, LimitBytes = int.MaxValue }, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Availability.ShouldBe(AgentRunLogReadAvailability.Available);
        result.Content.ShouldBe(new byte[] { 1, 2, 3 });
        result.NextOffsetBytes.ShouldBe(20);
        result.HasMore.ShouldBeTrue();
        service.RangeRequest.ShouldNotBeNull();
        service.RangeRequest.TeamId.ShouldBe(teamId);
        service.RangeRequest.Length.ShouldBe(1024 * 1024);
    }

    [Fact]
    public async Task Read_preserves_typed_unavailability_instead_of_returning_empty_content()
    {
        var runId = Guid.NewGuid();
        var metadata = Metadata(runId);
        var service = new StubLogService
        {
            Metadata = new AgentRunLogMetadataResult.Found(metadata),
            Range = new AgentRunLogRangeResult.Unavailable(new AgentRunLogProblem(AgentRunLogProblemCode.ArtifactCorrupt), metadata),
        };
        var handler = new ReadAgentRunLogRangeQueryHandler(service, new StubCurrentTeam(Guid.NewGuid()));

        var result = await handler.Handle(new ReadAgentRunLogRangeQuery { AgentRunId = runId, StreamId = metadata.StreamId }, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Availability.ShouldBe(AgentRunLogReadAvailability.IntegrityFailure);
        result.Content.ShouldBeEmpty();
        result.ProblemCode.ShouldBe(nameof(AgentRunLogProblemCode.ArtifactCorrupt));
    }

    [Fact]
    public void Wire_projection_never_exposes_capture_error_messages_or_claim_identity()
    {
        var entity = new AgentRunLogStream
        {
            Id = Guid.NewGuid(), AgentRunId = Guid.NewGuid(), StreamKind = AgentRunLogKinds.StandardError,
            ContentType = "text/plain", CaptureSource = "durable-spool/v1", Retention = ArtifactRetention.Run,
            State = AgentRunLogStreamState.CaptureFailed, Revision = 2, ErrorCode = "backend_unavailable",
            ErrorMessage = "secret-bearing provider detail", WorkerFenceEpoch = 42, CaptureSessionId = Guid.NewGuid(),
        };

        var wire = System.Text.Json.JsonSerializer.Serialize(AgentRunLogWire.Project(entity));

        wire.ShouldNotContain("secret-bearing provider detail");
        wire.ShouldNotContain("CaptureSession");
        wire.ShouldNotContain("WorkerFence");
    }

    private static AgentRunLogMetadata Metadata(Guid runId) => new(Guid.NewGuid(), runId, AgentRunLogKinds.StandardOutput, "text/plain", "utf-8", "durable-spool/v1", ArtifactRetention.Run, AgentRunLogStreamState.Open, 1, 0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);

    private sealed class StubLogService : IAgentRunLogService
    {
        public AgentRunLogMetadataResult Metadata { get; init; } = new AgentRunLogMetadataResult.Missing();
        public AgentRunLogRangeResult Range { get; init; } = new AgentRunLogRangeResult.Unavailable(new AgentRunLogProblem(AgentRunLogProblemCode.Missing));
        public Guid MetadataTeamId { get; private set; }
        public AgentRunLogRangeRequest? RangeRequest { get; private set; }

        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken)
        {
            MetadataTeamId = teamId;
            return Task.FromResult(Metadata);
        }

        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken)
        {
            RangeRequest = request;
            return Task.FromResult(Range);
        }

        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class StubCurrentTeam(Guid id) : ICurrentTeam
    {
        public Guid? Id { get; } = id;
        public bool IsSet => true;
    }
}
