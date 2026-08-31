using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// The whole path a STANDALONE Agent Run's lost frame travels to reach a human: the real capture plane refuses a real
/// durable write, the gap it records is keyed to the Agent Run that owns it, and
/// <c>GET /api/agents/runs/{id}</c> hands it back through the REAL ASP.NET pipeline — JWT auth, the X-Team-Id scope,
/// the controller, the mediator, the bounded observation reader.
///
/// <para><b>Why this run and not any other.</b> A standalone run has no workflow run, and while the gap plane demanded
/// one this was the run whose losses were invisible end to end: no manifest could carry its facet AND no gap could
/// carry its owner, so the operator's answer to "did this run lose anything?" was an empty list either way. An empty
/// list that means "nothing was lost" and an empty list that means "nothing could be recorded" are the two answers this
/// plane exists to keep apart, and only a live read can show they now differ.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host, real Postgres, real capture plane, real refusal.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StandaloneRunCaptureGapEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string PricedModel = "claude-sonnet-4-6";

    private readonly TaskLaunchApiFactory _factory;

    public StandaloneRunCaptureGapEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_standalone_run_that_lost_a_frame_shows_its_gap()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var run = await SeedStandaloneRunAsync(teamId);

        using (var quiet = await ReadCaptureGapsAsync(run.AgentRunId, userId, teamId))
        {
            quiet.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0,
                customMessage: "the premise: this run has lost nothing yet, so an item below is a span it really lost and not one that was always there");
        }

        var handle = await LoseAFrameAsync(run);

        using var observed = await ReadCaptureGapsAsync(run.AgentRunId, userId, teamId);
        var gaps = observed.RootElement;

        gaps.GetProperty("availability").GetString().ShouldBe(nameof(AgentRunCaptureGapReadAvailability.Available));
        gaps.GetProperty("truncated").GetBoolean().ShouldBeFalse();

        var item = gaps.GetProperty("items").EnumerateArray().ToList().ShouldHaveSingleItem();
        item.GetProperty("agentRunId").GetGuid().ShouldBe(run.AgentRunId,
            customMessage: "the run that owns the record is the run the gap names, and it is the only key this run has");
        item.GetProperty("harnessProcessAttemptId").GetGuid().ShouldBe(handle.AttemptId);
        item.GetProperty("attemptWorkerFenceEpoch").GetInt64().ShouldBe(handle.WorkerFenceEpoch);
        item.GetProperty("subjectKind").GetString().ShouldBe(WorkflowRunDataOwnerKinds.NativeRecord);
        item.GetProperty("reason").GetString().ShouldBe(nameof(CaptureGapReason.WriteRefused));
        item.GetProperty("rangeKind").GetString().ShouldBe(nameof(CaptureGapRangeKind.Ordinal));
        item.GetProperty("rangeStart").GetInt64().ShouldBe(1,
            customMessage: "the first frame of the refused batch is where this stream's records stop, and it is what a human needs to find the hole");
        item.GetProperty("resolution").GetString().ShouldBe(nameof(CaptureGapResolution.Open));
    }

    /// <summary>
    /// The loss, made real rather than staged: ordinal 1 is already recorded on this stream, so
    /// <c>ux_workflow_run_native_record_ordinal</c> refuses the batch that re-delivers it. The frames of that batch
    /// never become records, and the plane records the span they would have filled.
    /// </summary>
    private async Task<NativeRecordCaptureHandle> LoseAFrameAsync(SeededRun run)
    {
        using var scope = _factory.Services.CreateScope();
        var plane = scope.ServiceProvider.GetRequiredService<INativeRecordPlane>();

        var opened = await plane.OpenAsync(new NativeRecordCaptureRequest
        {
            TeamId = run.TeamId, AgentRunId = run.AgentRunId, HarnessTypeKey = "claude-code/v2", RunnerKind = "local",
            RunnerLocatorJson = "{\"spoolKey\":\"round-0\"}", WorkerFenceEpoch = run.FenceEpoch,
            Channel = NativeRecordChannel.Stdout,
        }, CancellationToken.None);

        var handle = opened.ShouldNotBeNull(customMessage: "the plane must open against the seeded run, or nothing below is being observed");
        handle.WorkflowRunId.ShouldBeNull(customMessage: "the premise: this Agent Run belongs to no workflow run");

        await plane.WriteAsync(Batch(handle, Frame(handle, 0), Frame(handle, 1)), CancellationToken.None);
        await Should.ThrowAsync<DbUpdateException>(() => plane.WriteAsync(Batch(handle, Frame(handle, 1), Frame(handle, 2)), CancellationToken.None));

        return handle;
    }

    private async Task<JsonDocument> ReadCaptureGapsAsync(Guid agentRunId, Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agents/runs/{agentRunId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await _factory.CreateClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"expected 200 but got {(int)response.StatusCode}; body: {body}");

        return JsonDocument.Parse(JsonDocument.Parse(body).RootElement.GetProperty("captureGaps").GetRawText());
    }

    private static NativeRecordBatch Batch(NativeRecordCaptureHandle handle, params NativeRecordV1[] frames) => new()
    {
        Handle = handle,
        Records = frames.Select(frame => new NativeRecordCapture { Frame = frame, Normalization = NativeRecordNormalization.Projected }).ToList(),
        Events = Array.Empty<AgentSemanticEventV1>(),
    };

    private static NativeRecordV1 Frame(NativeRecordCaptureHandle handle, long ordinal)
    {
        var payload = $"{{\"type\":\"assistant\",\"ordinal\":{ordinal}}}";

        return new NativeRecordV1
        {
            ContractVersion = WorkflowRunDataContract.CurrentVersion, RecordId = Guid.NewGuid(), StreamId = handle.StreamId,
            Ordinal = ordinal, Channel = handle.Channel, NativeType = "assistant", IngestedAt = DateTimeOffset.UtcNow,
            ByteOffset = ordinal * 512, ByteLength = Encoding.UTF8.GetByteCount(payload), InlinePayload = payload,
            DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
            Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            SizeBytes = Encoding.UTF8.GetByteCount(payload), Encoding = NativeRecordPayloadEncoding.Utf8,
            Redaction = NativeRecordRedaction.None, IsFinal = true,
        };
    }

    private async Task<SeededRun> SeedStandaloneRunAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IAgentRunService>();
        var created = await runs.CreateAsync(
            new AgentTask { Goal = "lose a frame with no workflow run to blame", Harness = ClaudeCodeHarness.HarnessKind, Model = PricedModel, TimeoutSeconds = 1800 },
            teamId, workflowRunId: null, nodeId: null, iterationKey: "", CancellationToken.None);

        // The run must be CLAIMED before capture may open against it: the opening carries the claim epoch, and 0137
        // refuses epoch 0 outright.
        return new SeededRun(teamId, created.Id, await runs.MarkRunningAsync(created.Id, CancellationToken.None));
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamMembershipAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { SecurityStamp = TestToken.SeedStamp, Id = userId, Email = $"gap-{suffix}@test.local", Name = "E2E", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"gap-{suffix}", Name = "E2E", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();

        return (userId, teamId);
    }

    private sealed record SeededRun(Guid TeamId, Guid AgentRunId, long FenceEpoch);
}
