using System.Security.Cryptography;
using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The harness model-call projection against the REAL plane and real Postgres (Rule 12 high fidelity), because every
/// invariant that makes this projection safe is a database one: the model-call plane's own CHECK constraints, 0130's
/// admission triggers on the non-record arm this source takes, and the two unique indexes that make re-projection a
/// no-op. A unit-tier test can show the projector reads a frame honestly; only this tier can show the rows it produces
/// are ones the database accepts, and that writing them twice does not double a cost.
///
/// <para><b>What only this tier can execute</b>, named so its absence from a local unit run is not mistaken for
/// coverage: that 0145's declared-unavailable CHECK admits the sets this projector writes; that 0130's attempt guard
/// accepts an attempt whose source kind is not <c>workflow-run-record/v1</c> and which therefore names no run record;
/// that a harness call row satisfies the composite parent FK and the workflow-run FK; that the re-delivered frame
/// of one provider response lands one call and one attempt rather than two; and that no semantic event ever names a
/// model-call row the write did not leave behind — the one defect a unit tier cannot see at all, because it only exists
/// once the writer has decided what to skip.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HarnessModelCallProjectionFlowTests
{
    private const string PricedModel = "claude-sonnet-4-6";
    private const string NodeId = "implement";
    private const string IterationKey = "implement#2";

    private static readonly ClaudeCodeHarness Claude = new();

    private readonly PostgresFixture _fixture;

    public HarnessModelCallProjectionFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The headline: a call an agent CLI made inside itself becomes a logical call with its physical attempt in the SAME
    /// tables a workflow LLM node's calls land in — with the model named, the tokens the harness stated, a cost from the
    /// shared pricing, and every figure the frame could not supply declared rather than zeroed.
    /// </summary>
    [Fact]
    public async Task A_harness_frame_becomes_a_logical_call_with_its_physical_attempt()
    {
        var run = await SeedWorkflowBoundRunAsync();
        using var planeScope = _fixture.BeginScope();
        var plane = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(plane, run);
        var frame = Frame(handle, ResponseFrame(), ordinal: 0);

        await WriteAsync(plane, handle, frame);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var call = await db.WorkflowRunModelCall.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(candidate => candidate.ModelCallId == call.Id);

        call.NodeId.ShouldBe(NodeId, customMessage: "the call belongs to the workflow cell the agent run is executing for, which is what makes it comparable with that cell's own node calls");
        call.IterationKey.ShouldBe(IterationKey);
        call.SourceKind.ShouldBe("harness-native-record/v1");
        call.Purpose.ShouldBe("harness-inference/v1");
        call.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        call.RequestedModel.ShouldBeNull("a response record states what was SERVED, never what was requested — filling this would invent a route");

        attempt.AttemptOrdinal.ShouldBe(1);
        attempt.EffectiveModel.ShouldBe(PricedModel, customMessage: "'which model did what' is the question the per-run aggregate cannot answer, and this column is the answer");
        attempt.TransportKind.ShouldBe("harness-native/v1");
        attempt.Status.ShouldBe("Succeeded");
        attempt.FinishReason.ShouldBe("end_turn");
        attempt.InputTokens.ShouldBe(1200);
        attempt.OutputTokens.ShouldBe(340);
        attempt.CacheReadTokens.ShouldBe(8000);
        attempt.CacheWriteTokens.ShouldBe(64);
        attempt.CostAmount.ShouldBe(AgentCostPricing.CostUsd(PricedModel, 1200, 340));
        attempt.CostCurrency.ShouldBe("USD");
        attempt.SourceNativeRecordId.ShouldBe(frame.RecordId, customMessage: "the frame is the row's whole evidence, so provenance is a column rather than a join a reader has to know to make");
        attempt.SourceTerminalRecordId.ShouldBeNull("this row projects no workflow-run record, and 0130's guard refuses one that claimed to");
        attempt.SourceEvidenceRevision.ShouldBe(0);
        (await db.WorkflowRunModelCallBodyCapture.CountAsync(value => value.ModelCallAttemptId == attempt.Id)).ShouldBe(0,
            "workflow-run-record body declarations never reinterpret or mix harness-native evidence");
        attempt.UnavailableFigures.ShouldBe(new[]
        {
            ModelCallFigures.CompletedAt, ModelCallFigures.FirstTokenAt,
            ModelCallFigures.ProviderRequestId, ModelCallFigures.ReasoningTokens,
        }, customMessage: "the CLI prints no request id and no timing, and the row must say so rather than leave a NULL nobody can interpret");
        attempt.ProviderRequestId.ShouldBeNull();
        attempt.CompletedAt.ShouldBeNull("repeating the ingest instant here would claim a call of zero duration");
        // Compared to the microsecond, which is all a timestamptz round-trip preserves: .NET keeps 100-ns ticks, Postgres
        // does not, so an exact compare depends on the ingest instant happening to land on a whole microsecond. macOS's
        // clock granularity makes that always true and Linux's does not, so this passed locally and failed in CI.
        attempt.StartedAt.ShouldBe(frame.IngestedAt, TimeSpan.FromMicroseconds(1), "the row must carry the frame's own ingest instant, to the precision the column stores");
    }

    /// <summary>
    /// Re-projection, at the tier that owns it. The capture seam re-delivers frames across a worker replacement, so the
    /// SAME provider response arrives again as a new frame at a new position. It must land no second call and no second
    /// attempt, or the run's cost silently doubles.
    /// </summary>
    [Fact]
    public async Task The_same_response_delivered_twice_lands_one_call_and_one_attempt()
    {
        var run = await SeedWorkflowBoundRunAsync();
        using var planeScope = _fixture.BeginScope();
        var plane = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(plane, run);

        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(), ordinal: 0));
        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(), ordinal: 1));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.AgentRunId == run.AgentRunId)).ShouldBe(2,
            "both frames were delivered and the capture floor records what it was given");
        (await db.WorkflowRunModelCall.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId)).ShouldBe(1,
            "one provider response is one call however many frames carried it");
        (await db.WorkflowRunModelCallAttempt.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId)).ShouldBe(1);

        var called = await db.WorkflowRunModelCall.AsNoTracking().Where(candidate => candidate.WorkflowRunId == run.WorkflowRunId)
            .Select(candidate => candidate.Id).SingleAsync();

        var cited = await db.WorkflowRunSemanticEvent.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == run.AgentRunId && candidate.ModelCallId != null)
            .Select(candidate => candidate.ModelCallId!.Value).ToListAsync();

        cited.Count.ShouldBe(2, "both frames were projected, so both carry an event that names the call");
        cited.Distinct().ShouldHaveSingleItem().ShouldBe(called,
            customMessage: "the re-delivered frame's event must cite the row the write KEPT; a freshly minted id would leave it naming a row the dedupe declined to write");
    }

    /// <summary>Two different responses of one execution are two calls — collapsing them would under-report cost as badly as duplicating one over-reports it.</summary>
    [Fact]
    public async Task Two_responses_land_two_calls_each_with_its_own_attempt()
    {
        var run = await SeedWorkflowBoundRunAsync();
        using var planeScope = _fixture.BeginScope();
        var plane = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(plane, run);

        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(callId: "msg_01FIRST"), ordinal: 0));
        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(callId: "msg_01SECOND"), ordinal: 1));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunModelCall.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId)).ShouldBe(2);
        (await db.WorkflowRunModelCallAttempt.CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId)).ShouldBe(2);
    }

    /// <summary>
    /// Tokens with no price are an UNAVAILABLE cost in the row, not a zero one. This is the tier that proves the CHECK
    /// admits the declaration and that nothing coalesced the null on its way to the column.
    /// </summary>
    [Fact]
    public async Task An_unpriced_model_stores_no_cost_and_declares_it_unavailable()
    {
        AgentCostPricing.PriceFor("some-unpriced-gateway-model").ShouldBeNull("the premise of this test: the platform has no price for that model");

        var run = await SeedWorkflowBoundRunAsync();
        using var planeScope = _fixture.BeginScope();
        var plane = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(plane, run);

        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(model: "some-unpriced-gateway-model"), ordinal: 0));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var attempt = await db.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);

        attempt.CostAmount.ShouldBeNull("a zero cost for an unpriced model is a wrong number that looks measured, and it sums silently into a run total");
        attempt.CostCurrency.ShouldBeNull();
        attempt.PricingVersion.ShouldBeNull();
        attempt.UnavailableFigures.ShouldContain(ModelCallFigures.CostAmount);
        attempt.InputTokens.ShouldBe(1200, customMessage: "the tokens ARE observed; it is only the price that is missing");
    }

    /// <summary>
    /// The invariant the whole join rests on, over a stream that exercises every arm of the writer's decision: a new
    /// response, a second one, and a re-delivery of that second. Every event that NAMES a model call must resolve to a
    /// row. An id pointing at nothing is worse than no id — a reader joins on it and reads the miss as a data gap rather
    /// than as an absence — and only this tier can see it, because the id dangles exactly when the writer decided to
    /// skip an insert.
    /// </summary>
    [Fact]
    public async Task No_semantic_event_names_a_model_call_row_the_write_did_not_leave_behind()
    {
        var run = await SeedWorkflowBoundRunAsync();
        using var planeScope = _fixture.BeginScope();
        var plane = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(plane, run);

        handle.WorkflowRunId.ShouldBe(run.WorkflowRunId, customMessage: "the opening reads its scope off the Agent Run, and the projector reads it off the opening");

        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(callId: "msg_01FIRST"), ordinal: 0));
        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(callId: "msg_01SECOND"), ordinal: 1));
        await WriteAsync(plane, handle, Frame(handle, ResponseFrame(callId: "msg_01SECOND"), ordinal: 2));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var named = await db.WorkflowRunSemanticEvent.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == run.AgentRunId && candidate.ModelCallId != null)
            .Select(candidate => candidate.ModelCallId!.Value).ToListAsync();
        var written = await db.WorkflowRunModelCall.AsNoTracking()
            .Where(candidate => candidate.WorkflowRunId == run.WorkflowRunId)
            .Select(candidate => candidate.Id).ToListAsync();

        named.Count.ShouldBe(3, "all three frames are the harness's own record of a call, so all three events name one");
        written.Count.ShouldBe(2, "the third frame re-delivers the second response, which is one call however many frames carried it");
        named.Except(written).ShouldBeEmpty(
            "an event naming a model_call row that does not exist is a silent gap every downstream join inherits, and it is worse than an event naming none");
    }

    /// <summary>
    /// The resumed opening carries the same workflow-run scope the launch did, so a re-attach keeps projecting calls
    /// instead of silently stopping. That scope is what decides whether a call may be minted at all, so a resume that
    /// answered null there would turn a worker replacement into a hole in the cost record with nothing saying so.
    /// </summary>
    [Fact]
    public async Task A_resumed_opening_carries_the_same_workflow_run_as_the_launch()
    {
        var run = await SeedWorkflowBoundRunAsync();
        using var planeScope = _fixture.BeginScope();
        var plane = planeScope.Resolve<INativeRecordPlane>();
        var launched = await OpenAsync(plane, run);

        var reopened = await ((INativeRecordExecutionPlane)plane).ReopenAsync(Request(run) with { Resume = true }, CancellationToken.None);
        var resumed = reopened.ShouldNotBeNull("the launch left a Running process, which is exactly what a re-attach resumes").Handle;

        resumed.ExecutionId.ShouldBe(launched.ExecutionId, customMessage: "a re-attach observes the process the replaced worker left running");
        resumed.WorkflowRunId.ShouldBe(run.WorkflowRunId,
            customMessage: "the scope a call may be minted against has to survive the worker replacement, or the resumed round records the frames and loses the calls");
        GroundedModelCallProjector.Project(Claude, resumed, Frame(resumed, ResponseFrame(), ordinal: 0)).ShouldNotBeNull();
    }

    /// <summary>
    /// A standalone agent run has no workflow run, and the model-call plane is keyed to one. Its frames are still
    /// recorded — and NOTHING is projected from them, not even the event that would cite a call, because an event naming
    /// a row nothing can write is worse than one naming none.
    /// </summary>
    [Fact]
    public async Task A_run_that_belongs_to_no_workflow_run_records_its_frames_and_names_no_call()
    {
        var run = await SeedStandaloneRunAsync();
        using var planeScope = _fixture.BeginScope();
        var plane = planeScope.Resolve<INativeRecordPlane>();
        var handle = await OpenAsync(plane, run);
        var frame = Frame(handle, ResponseFrame(), ordinal: 0);

        handle.WorkflowRunId.ShouldBeNull("the premise: the opening reads its scope off the Agent Run, and this run belongs to no workflow run");
        GroundedModelCallProjector.Project(Claude, handle, frame).ShouldBeNull(
            "the frame IS the harness's own record of a call; what it cannot be is a row of a run-keyed plane, so no identity is minted for one");

        await plane.WriteAsync(new NativeRecordBatch
        {
            Handle = handle,
            Records = new[] { new NativeRecordCapture { Frame = frame, Normalization = NativeRecordNormalization.Projected } },
            Events = Array.Empty<AgentSemanticEventV1>(),
        }, CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.AgentRunId == run.AgentRunId)).ShouldBe(1,
            "the capture floor is untouched: the frame is recorded whether or not a workflow run exists to key a call to");
        (await db.WorkflowRunSemanticEvent.CountAsync(candidate => candidate.AgentRunId == run.AgentRunId && candidate.ModelCallId != null)).ShouldBe(0,
            "an event that named a call here could never resolve, since the plane has no row for a run-less call to be");
        (await db.WorkflowRunModelCallAttempt.CountAsync(candidate => candidate.TeamId == run.TeamId)).ShouldBe(0,
            "the plane is keyed to a workflow run, so a standalone run's calls are not projected rather than attached to an invented parent");
    }

    private static string ResponseFrame(string callId = "msg_01PINNED", string model = PricedModel) =>
        $"{{\"type\":\"assistant\",\"message\":{{\"id\":\"{callId}\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"{model}\","
        + "\"content\":[{\"type\":\"text\",\"text\":\"working\"}],\"stop_reason\":\"end_turn\","
        + "\"usage\":{\"input_tokens\":1200,\"output_tokens\":340,\"cache_read_input_tokens\":8000,\"cache_creation_input_tokens\":64}}}";

    /// <summary>One write carrying the frame, its exactly grounded event and the model call it records — the shape the pump hands the plane.</summary>
    private static async Task WriteAsync(INativeRecordPlane plane, NativeRecordCaptureHandle handle, NativeRecordV1 frame)
    {
        var projection = GroundedModelCallProjector.Project(Claude, handle, frame).ShouldNotBeNull();

        await plane.WriteAsync(new NativeRecordBatch
        {
            Handle = handle,
            Records = new[] { new NativeRecordCapture { Frame = frame, Normalization = NativeRecordNormalization.Projected } },
            Events = new[] { GroundedModelCallProjector.NamedEvent(handle, projection) },
            ModelCalls = new[] { projection },
        }, CancellationToken.None);
    }

    private static async Task<NativeRecordCaptureHandle> OpenAsync(INativeRecordPlane plane, SeededRun run)
    {
        var opened = await plane.OpenAsync(Request(run), CancellationToken.None);

        return opened.ShouldNotBeNull("the plane must open against the seeded run, or the test is asserting nothing");
    }

    private static NativeRecordCaptureRequest Request(SeededRun run) => new()
    {
        TeamId = run.TeamId,
        AgentRunId = run.AgentRunId,
        HarnessTypeKey = "claude-code/v2",
        RunnerKind = "local",
        RunnerLocatorJson = "{\"spoolKey\":\"round-0\"}",
        WorkerFenceEpoch = run.FenceEpoch,
        Channel = NativeRecordChannel.Stdout,
    };

    private static NativeRecordV1 Frame(NativeRecordCaptureHandle handle, string payload, long ordinal) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion, RecordId = Guid.NewGuid(), StreamId = handle.StreamId,
        Ordinal = ordinal, Channel = handle.Channel, NativeType = "assistant", IngestedAt = DateTimeOffset.UtcNow,
        ByteOffset = ordinal * 512, ByteLength = Encoding.UTF8.GetByteCount(payload), InlinePayload = payload,
        DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
        Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
        SizeBytes = Encoding.UTF8.GetByteCount(payload), Encoding = NativeRecordPayloadEncoding.Utf8,
        Redaction = NativeRecordRedaction.None, IsFinal = true,
    };

    private async Task<SeededRun> SeedWorkflowBoundRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "harness-model-call-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        var workflowRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        return await CreateAgentRunAsync(teamId, workflowRunId);
    }

    private async Task<SeededRun> SeedStandaloneRunAsync()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        return await CreateAgentRunAsync(teamId, workflowRunId: null);
    }

    private async Task<SeededRun> CreateAgentRunAsync(Guid teamId, Guid? workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var created = await runs.CreateAsync(
            new AgentTask { Goal = "record its own model calls", Harness = ClaudeCodeHarness.HarnessKind, Model = PricedModel, TimeoutSeconds = 1800 },
            teamId, workflowRunId, workflowRunId is null ? null : NodeId,
            workflowRunId is null ? "" : IterationKey, CancellationToken.None);

        // The run must be CLAIMED before capture may open against it: a capture opening carries the claim epoch, and
        // 0137 refuses epoch 0 outright, so a run left Queued cannot have a process attempt at all. This is the epoch
        // the executor opens under.
        var fenceEpoch = await runs.MarkRunningAsync(created.Id, CancellationToken.None);

        return new SeededRun(teamId, created.Id, workflowRunId ?? Guid.Empty, fenceEpoch);
    }

    private sealed record SeededRun(Guid TeamId, Guid AgentRunId, Guid WorkflowRunId, long FenceEpoch);
}
