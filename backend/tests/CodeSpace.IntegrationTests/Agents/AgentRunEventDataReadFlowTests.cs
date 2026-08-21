using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunEventDataReadFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunEventDataReadFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Owning_attempt_rehydrates_the_complete_offloaded_event_payload_through_bounded_pages()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await CreateRunAsync(teamId);
        var raw = JsonSerializer.Serialize(new { name = "WebSearch", input = new { query = "tool telemetry" }, result = new string('x', 12_000), sentinel = "END" });
        var appended = await AppendAsync(runId, JsonSerializer.Deserialize<JsonElement>(raw));

        appended.DataJson.ShouldBeNull("the test must exercise the offloaded carrier");
        appended.DataArtifactId.ShouldNotBeNull();

        using var output = new MemoryStream();
        long offset = 0;
        do
        {
            var page = await ReadAsync(userId, teamId, runId, appended.Sequence, offset, 4 * 1024);
            page.ShouldNotBeNull();
            page!.Availability.ShouldBe(AgentRunEventDataReadAvailability.Available);
            page.AgentRunId.ShouldBe(runId);
            page.EventSequence.ShouldBe(appended.Sequence);
            page.DataArtifactId.ShouldBe(appended.DataArtifactId);
            page.OffsetBytes.ShouldBe(offset);
            page.ReturnedBytes.ShouldBeInRange(1, 4 * 1024);
            page.TotalBytes.ShouldBe(Encoding.UTF8.GetByteCount(raw));
            page.ContentType.ShouldBe("application/json");
            page.Sha256.ShouldNotBeNullOrWhiteSpace();
            page.IntegrityVerified.ShouldBeFalse("a partial provider range must not claim whole-object verification");
            await output.WriteAsync(page.Content);
            offset = page.NextOffsetBytes ?? page.TotalBytes!.Value;
        } while (offset < Encoding.UTF8.GetByteCount(raw));

        Encoding.UTF8.GetString(output.ToArray()).ShouldBe(raw, "bounded pages must losslessly reconstruct the full canonical payload");
    }

    [Fact]
    public async Task Event_data_identity_is_scoped_to_the_exact_team_run_and_sequence()
    {
        var (ownerTeam, ownerUser) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeam, foreignUser) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var ownerRun = await CreateRunAsync(ownerTeam);
        var otherOwnerRun = await CreateRunAsync(ownerTeam);
        var appended = await AppendAsync(ownerRun, JsonSerializer.SerializeToElement(new { body = new string('x', 12_000) }));

        (await ReadAsync(ownerUser, ownerTeam, otherOwnerRun, appended.Sequence, 0, 1024)).ShouldBeNull("an event sequence cannot be borrowed by another attempt in the same tenant");
        (await ReadAsync(foreignUser, foreignTeam, ownerRun, appended.Sequence, 0, 1024)).ShouldBeNull("foreign and absent identities remain 404-conflated");
        (await ReadAsync(ownerUser, ownerTeam, ownerRun, long.MaxValue, 0, 1024)).ShouldBeNull("an absent sequence does not disclose the run");
    }

    [Fact]
    public async Task Existing_events_report_not_referenced_missing_artifact_and_invalid_range_as_typed_unavailable()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await CreateRunAsync(teamId);
        var inline = await AppendAsync(runId, JsonSerializer.SerializeToElement(new { name = "Read", path = "README.md" }));

        var notReferenced = await ReadAsync(userId, teamId, runId, inline.Sequence, 0, 1024);
        notReferenced!.Availability.ShouldBe(AgentRunEventDataReadAvailability.NotReferenced);
        notReferenced.IsRetryable.ShouldBeFalse();
        notReferenced.Content.ShouldBeEmpty();

        AgentRunEvent missing;
        using (var scope = _fixture.BeginScope())
        {
            missing = new AgentRunEvent
            {
                Id = Guid.NewGuid(), AgentRunId = runId, Kind = AgentEventKind.ToolCall, Text = "missing payload", DataArtifactId = Guid.NewGuid(),
            };
            scope.Resolve<CodeSpaceDbContext>().AgentRunEvent.Add(missing);
            await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }

        var absent = await ReadAsync(userId, teamId, runId, missing.Sequence, 0, 1024);
        absent!.Availability.ShouldBe(AgentRunEventDataReadAvailability.MetadataMissing);
        absent.IsRetryable.ShouldBeFalse();
        absent.Content.ShouldBeEmpty();

        var large = await AppendAsync(runId, JsonSerializer.SerializeToElement(new { body = new string('y', 12_000) }));
        var invalid = await ReadAsync(userId, teamId, runId, large.Sequence, long.MaxValue, 2);
        invalid!.Availability.ShouldBe(AgentRunEventDataReadAvailability.InvalidRange);
        invalid.IsRetryable.ShouldBeFalse();
        invalid.Content.ShouldBeEmpty();
    }

    private async Task<Guid> CreateRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "Capture tool telemetry", Harness = "codex-cli" },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
    }

    private async Task<AgentRunEvent> AppendAsync(Guid runId, JsonElement data)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunService>().AppendEventAsync(runId,
            new AgentEvent { Kind = AgentEventKind.ToolCall, Text = "tool", Data = data }, CancellationToken.None);
    }

    private async Task<AgentRunEventDataRangeRead?> ReadAsync(Guid userId, Guid teamId, Guid runId, long sequence, long offsetBytes, int limitBytes)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ReadAgentRunEventDataRangeQuery
        {
            AgentRunId = runId, EventSequence = sequence, OffsetBytes = offsetBytes, LimitBytes = limitBytes,
        });
    }
}
