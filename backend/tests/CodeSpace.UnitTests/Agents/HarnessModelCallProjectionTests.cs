using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The honesty contract of the harness model-call projection, which is the whole value of the lane. A harness CLI's
/// internal calls never touch <c>ILLMClient</c>, so the only evidence a particular call happened is a frame the CLI
/// printed about it — and the difference between a frame that IS the harness's record of a call and a frame that merely
/// MENTIONS a model is the difference between a cost report and a fabricated one. Every test here pins one half of that:
/// what is projected, and what is refused.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessModelCallProjectionTests
{
    private const string PricedModel = "claude-sonnet-4-6";

    private static readonly ClaudeCodeHarness Claude = new();

    /// <summary>An assistant frame whose <c>message</c> is a complete provider response, with cache figures and a stop reason.</summary>
    private static string ResponseFrame(string callId = "msg_01PINNED", string model = PricedModel) =>
        $"{{\"type\":\"assistant\",\"message\":{{\"id\":\"{callId}\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"{model}\","
        + "\"content\":[{\"type\":\"text\",\"text\":\"working\"}],\"stop_reason\":\"end_turn\","
        + "\"usage\":{\"input_tokens\":1200,\"output_tokens\":340,\"cache_read_input_tokens\":8000,\"cache_creation_input_tokens\":64}},"
        + "\"session_id\":\"0f4d1a2e-9c31-4a7b-9f8e-2b1d5c7a4e60\"}";

    [Fact]
    public void A_frame_that_is_the_harnesss_own_response_record_yields_a_call_with_its_stated_figures()
    {
        var handle = Handle();
        var record = Frame(ResponseFrame());

        var projection = GroundedModelCallProjector.Project(Claude, handle, record).ShouldNotBeNull(
            "the assistant envelope's message IS one provider response — it is the only per-call evidence a CLI's internal call leaves");

        projection.Model.ShouldBe(PricedModel, customMessage: "the row's model is the one the response was SERVED by, which is exactly the fact the per-run aggregate cannot supply");
        projection.InputTokens.ShouldBe(1200);
        projection.OutputTokens.ShouldBe(340);
        projection.CacheReadTokens.ShouldBe(8000);
        projection.CacheWriteTokens.ShouldBe(64);
        projection.FinishReason.ShouldBe("end_turn");
        projection.TransportKind.ShouldBe(GroundedModelCallProjector.TransportKind);
        projection.SourceKind.ShouldBe(GroundedModelCallProjector.SourceKind);
        projection.SourceNativeRecordId.ShouldBe(record.RecordId, customMessage: "the frame is the row's whole evidence, so the row must name it");
        projection.CallOrdinal.ShouldBe(record.Ordinal + 1);
        projection.ObservedAt.ShouldBe(record.IngestedAt);
        projection.Completeness.ShouldBe(WorkflowRunCaptureCompleteness.Partial,
            customMessage: "however verbatim the bytes, a row several of whose columns the frame could not supply is not a complete record of the call");
        projection.Validate().ShouldBeEmpty();
    }

    /// <summary>
    /// The exactness claim: a fact read out of a VERBATIM captured frame may be projected as the harness's own words, and
    /// the event carries the call id so the semantic plane and the model-call plane join on one frame's bytes.
    /// </summary>
    [Fact]
    public void The_projection_beside_the_call_is_exactly_grounded_in_the_frame_it_cites()
    {
        var handle = Handle();
        var record = Frame(ResponseFrame());

        var projection = GroundedModelCallProjector.Project(Claude, handle, record)!;
        var evented = GroundedModelCallProjector.NamedEvent(handle, projection);

        projection.Fidelity.ShouldBe(SemanticProjectionQuality.Exact);
        evented.ProjectionQuality.IsExactlyGrounded().ShouldBeTrue(
            "the figures were transcribed from the harness's own record, not normalized out of a display line");
        evented.SourceNativeRecordIds.ShouldBe(new[] { record.RecordId });
        evented.ModelCallId.ShouldBe(projection.ModelCallId, customMessage: "without the call id on the event, 'which frame is this cost from' needs a join nobody can make");
        evented.EventType.ShouldBe("https://codespace.dev/agent/v1/harness-model-called");
        evented.Necessity.ShouldBe(SemanticEventNecessity.Ignorable);
        evented.Validate().ShouldBeEmpty();
    }

    /// <summary>Masked bytes back a redacted-exact claim and nothing stronger; bytes never captured back nothing at all, so no row is written from a frame there is no evidence for.</summary>
    [Theory]
    [InlineData(NativeRecordRedaction.None, SemanticProjectionQuality.Exact)]
    [InlineData(NativeRecordRedaction.Masked, SemanticProjectionQuality.RedactedExact)]
    public void Fidelity_can_never_outrun_the_bytes_that_were_captured(NativeRecordRedaction redaction, SemanticProjectionQuality expected)
    {
        var projection = GroundedModelCallProjector.Project(Claude, Handle(), Frame(ResponseFrame()) with { Redaction = redaction });

        projection.ShouldNotBeNull();
        projection.Fidelity.ShouldBe(expected);
    }

    /// <summary>
    /// A frame nobody captured backs nothing, and BOTH ways of arriving at that are refused: the metadata-only shape the
    /// contract actually produces, and the writer-bug shape that says Withheld while still carrying bytes — built by hand
    /// here, because <see cref="NativeRecordV1.Validate"/> refuses it and the projector must not depend on that having run.
    /// </summary>
    [Fact]
    public void A_frame_that_was_never_captured_yields_no_call_at_all()
    {
        var metadataOnly = Frame(ResponseFrame()) with { Redaction = NativeRecordRedaction.Withheld, InlinePayload = null };
        var contradictory = Frame(ResponseFrame()) with { Redaction = NativeRecordRedaction.Withheld };

        GroundedModelCallProjector.Project(Claude, Handle(), metadataOnly).ShouldBeNull(
            "a row read 'out of' bytes nobody captured could not have been read at all");
        GroundedModelCallProjector.Project(Claude, Handle(), contradictory).ShouldBeNull(
            "Withheld bytes support no fidelity claim, so the projector refuses them rather than reading a row it would then have to qualify as nothing");
    }

    /// <summary>
    /// The load-bearing refusal. Each of these frames NAMES a model and none of them records a call: the session line
    /// announces the configured model, the assistant text quotes one, and the terminal result line carries the whole
    /// RUN's usage. A row built from any of them would put a figure in a cost report that no single call ever spent.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"system","subtype":"init","session_id":"0f4d1a2e-9c31-4a7b-9f8e-2b1d5c7a4e60","model":"claude-sonnet-4-6","tools":[]}""")]
    [InlineData("""{"type":"assistant","message":{"type":"message","role":"assistant","content":[{"type":"text","text":"I will run this on claude-sonnet-4-6"}]}}""")]
    [InlineData("""{"type":"result","subtype":"success","result":"done","is_error":false,"model":"claude-sonnet-4-6","usage":{"input_tokens":920,"output_tokens":175}}""")]
    [InlineData("""{"type":"assistant","message":{"id":"msg_01X","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":[]}}""")]
    [InlineData("""{"type":"assistant","message":{"id":"msg_01X","type":"message","role":"assistant","content":[],"usage":{"input_tokens":5,"output_tokens":1}}}""")]
    [InlineData("""{"type":"assistant","message":{"type":"message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":5,"output_tokens":1}}}""")]
    [InlineData("a plain stdout line that mentions claude-sonnet-4-6")]
    public void A_frame_that_merely_mentions_a_model_yields_nothing(string payload)
    {
        Claude.ReadModelCallFrame(payload).ShouldBeNull(
            "a frame that names a model states nothing about any particular call, and a fabricated attempt row is worse than a missing one");
        GroundedModelCallProjector.Project(Claude, Handle(), Frame(payload)).ShouldBeNull();
    }

    /// <summary>
    /// A figure the record did not state is DECLARED unavailable, never stored as zero. Four are structural for a
    /// harness-observed call whatever the record says; the cache pair is conditional, which is what makes the declaration
    /// informative rather than boilerplate.
    /// </summary>
    [Fact]
    public void A_figure_the_record_does_not_state_is_recorded_as_unavailable_rather_than_zero()
    {
        const string withoutCacheFigures = """{"type":"assistant","message":{"id":"msg_01NOCACHE","type":"message","role":"assistant","model":"claude-sonnet-4-6","usage":{"input_tokens":10,"output_tokens":2}}}""";

        var projection = GroundedModelCallProjector.Project(Claude, Handle(), Frame(withoutCacheFigures)).ShouldNotBeNull();

        projection.CacheReadTokens.ShouldBeNull("a zero here would read as 'this call read no cache', which the record does not say");
        projection.CacheWriteTokens.ShouldBeNull();
        projection.UnavailableFigures.ShouldBe(new[]
        {
            ModelCallFigures.CacheReadTokens, ModelCallFigures.CacheWriteTokens,
            ModelCallFigures.CompletedAt, ModelCallFigures.FirstTokenAt,
            ModelCallFigures.ProviderRequestId, ModelCallFigures.ReasoningTokens,
        }, customMessage: "an unobserved figure must be named, canonically, or NULL cannot be told from 'not written yet'");
        projection.Validate().ShouldBeEmpty();
    }

    /// <summary>
    /// The four figures a harness-observed row can never carry, pinned so a later change that quietly starts filling one
    /// of them from the ingest instant has to face this assertion. <c>first_token_at</c> and <c>completed_at</c> are the
    /// dangerous pair: the ingest instant is available and repeating it there would claim a call of zero duration.
    /// </summary>
    [Fact]
    public void The_figures_a_harness_frame_can_never_carry_are_always_declared()
    {
        var projection = GroundedModelCallProjector.Project(Claude, Handle(), Frame(ResponseFrame())).ShouldNotBeNull();

        projection.UnavailableFigures.ShouldBe(new[]
        {
            ModelCallFigures.CompletedAt, ModelCallFigures.FirstTokenAt,
            ModelCallFigures.ProviderRequestId, ModelCallFigures.ReasoningTokens,
        }, customMessage: "the CLI prints no provider request id, no per-call reasoning count and no timing; the cache pair WAS stated here, so it is not declared");
    }

    /// <summary>
    /// Tokens without a price are an UNAVAILABLE cost, never a zero one. A zero cost sums silently into a run total and
    /// makes a deployment believe its unpriced models are free.
    /// </summary>
    [Fact]
    public void Tokens_without_a_price_yield_an_unavailable_cost_rather_than_zero()
    {
        var unpriced = GroundedModelCallProjector.Project(Claude, Handle(), Frame(ResponseFrame(model: "some-unpriced-gateway-model"))).ShouldNotBeNull();

        AgentCostPricing.PriceFor("some-unpriced-gateway-model").ShouldBeNull("the premise of this test: the platform has no price for that model");
        unpriced.CostAmount.ShouldBeNull("a zero cost for an unpriced model is a wrong number that looks measured");
        unpriced.CostCurrency.ShouldBeNull();
        unpriced.PricingVersion.ShouldBeNull("nothing priced this row, so naming a pricing version would claim otherwise");
        unpriced.UnavailableFigures.ShouldContain(ModelCallFigures.CostAmount);
        unpriced.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void A_priced_model_carries_its_cost_its_currency_and_what_priced_it()
    {
        var priced = GroundedModelCallProjector.Project(Claude, Handle(), Frame(ResponseFrame())).ShouldNotBeNull();

        priced.CostAmount.ShouldBe(AgentCostPricing.CostUsd(PricedModel, 1200, 340),
            customMessage: "a per-call cost must ride the SAME pricing the run total does, or the two disagree");
        priced.CostAmount.ShouldNotBeNull();
        priced.CostCurrency.ShouldBe("USD");
        priced.PricingVersion.ShouldBe(GroundedModelCallProjector.PricingVersion);
        priced.UnavailableFigures.ShouldNotContain(ModelCallFigures.CostAmount);
    }

    /// <summary>
    /// Idempotence, at the level it has to hold: BOTH identities are functions of the harness's OWN id for the response
    /// and the execution, so re-reading the same response — a frame the capture seam re-delivered across a re-attach, at
    /// a different position and with a fresh record id — yields the same admission key AND the same row id. The key is
    /// what stops a second billed call. The ROW ID is what stops the second, quieter defect: the plane skips a call it
    /// already holds, and the event projected beside the skipped one cites the row id regardless — freshly minted, that
    /// event would name a row nothing ever wrote.
    /// </summary>
    [Fact]
    public void Re_reading_the_same_response_yields_the_same_call_and_admission_identities()
    {
        var handle = Handle();
        var first = GroundedModelCallProjector.Project(Claude, handle, Frame(ResponseFrame()))!;
        var redelivered = GroundedModelCallProjector.Project(Claude, handle, Frame(ResponseFrame()) with
        {
            RecordId = Guid.NewGuid(), Ordinal = 41, ByteOffset = 9000, IngestedAt = first.ObservedAt.AddMinutes(3),
        })!;

        redelivered.SourceCorrelationId.ShouldBe(first.SourceCorrelationId,
            "the same provider response re-delivered must not become a second billed call");
        redelivered.ModelCallId.ShouldBe(first.ModelCallId,
            "the writer skips the call it already holds, so a re-delivered frame's event must cite THAT row rather than an id nothing will write");
        GroundedModelCallProjector.NamedEvent(handle, redelivered).ModelCallId.ShouldBe(first.ModelCallId);
    }

    /// <summary>The row id and the admission key are derived from the same response but are NOT the same value: a primary key and a producer's idempotence key are different columns, and one changing must not silently change the other.</summary>
    [Fact]
    public void The_call_row_id_and_the_admission_key_are_separate_derived_identities()
    {
        var projection = Projection();

        projection.ModelCallId.ShouldNotBe(projection.SourceCorrelationId);
        projection.ModelCallId.ShouldNotBe(Guid.Empty);
        projection.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void Two_different_responses_of_one_execution_are_two_calls()
    {
        var handle = Handle();
        var first = GroundedModelCallProjector.Project(Claude, handle, Frame(ResponseFrame(callId: "msg_01FIRST")))!;
        var second = GroundedModelCallProjector.Project(Claude, handle, Frame(ResponseFrame(callId: "msg_01SECOND")))!;

        second.SourceCorrelationId.ShouldNotBe(first.SourceCorrelationId,
            "collapsing two calls into one would under-report cost as badly as fabricating one over-reports it");
        second.ModelCallId.ShouldNotBe(first.ModelCallId);
    }

    [Fact]
    public void One_response_id_seen_under_two_executions_is_two_calls()
    {
        var record = Frame(ResponseFrame());
        var first = GroundedModelCallProjector.Project(Claude, Handle(), record)!;
        var second = GroundedModelCallProjector.Project(Claude, Handle(), record)!;

        second.SourceCorrelationId.ShouldNotBe(first.SourceCorrelationId,
            "a response id belongs to the harness, not to this platform, so two executions must not be able to collide on one");
        second.ModelCallId.ShouldNotBe(first.ModelCallId);
    }

    /// <summary>
    /// The other half of "the row it names exists": a run bound to no workflow run can have no row in a run-keyed plane,
    /// so no call is projected from its frames at all — and therefore no event cites an id, and the reduction that folds
    /// those events names none either. Projecting the call and letting the writer decline it would leave an id pointing
    /// at nothing permanently, which a reader joins on and reads as a data gap.
    /// </summary>
    [Fact]
    public void An_opening_that_belongs_to_no_workflow_run_projects_no_call()
    {
        var standalone = Handle() with { WorkflowRunId = null };

        GroundedModelCallProjector.Project(Claude, standalone, Frame(ResponseFrame())).ShouldBeNull(
            "the model-call plane is keyed to a workflow run, so a standalone run's frames may mint no call identity");
        GroundedModelCallProjector.Project(Claude, Handle(), Frame(ResponseFrame())).ShouldNotBeNull(
            "the premise of this test: the very same frame IS projected once an opening names a workflow run");
    }

    /// <summary>A record whose id or model the harness did not state is refused by the projector even if a harness hands it in directly — this is the one gate every projected call passes through.</summary>
    [Theory]
    [InlineData("", PricedModel)]
    [InlineData("  ", PricedModel)]
    [InlineData("msg_01X", "")]
    public void A_call_with_no_stated_identity_or_no_stated_model_is_not_projected(string callId, string model)
    {
        var harness = new StatingHarness(new GroundedModelCallFrame { CallId = callId, Model = model, InputTokens = 4, OutputTokens = 2 });

        GroundedModelCallProjector.Project(harness, Handle(), Frame("{}")).ShouldBeNull(
            "an unnamed call cannot be deduplicated and an unnamed model is exactly the row this plane exists to stop being the only one available");
    }

    /// <summary>A value the row cannot store faithfully is refused rather than truncated: a clipped model under an exact claim is the laundering this plane forbids, and it would also take the batch of frames down at commit.</summary>
    [Fact]
    public void A_stated_value_wider_than_its_column_is_refused_rather_than_truncated()
    {
        var harness = new StatingHarness(new GroundedModelCallFrame
        {
            CallId = "msg_01WIDE", Model = new string('m', 501), InputTokens = 4, OutputTokens = 2,
        });

        GroundedModelCallProjector.Project(harness, Handle(), Frame("{}")).ShouldBeNull();
    }

    /// <summary>A harness with no model-call reader contributes nothing, and the capture path is byte-identical to one where this plane does not exist.</summary>
    [Fact]
    public void A_harness_that_records_no_model_call_projects_nothing()
    {
        Codex().ShouldNotBeAssignableTo<IAgentModelCallFrameReader>(
            "Codex's --json stream names no model and reports only a cumulative per-turn total, so a per-call row from it would be invented");

        GroundedModelCallProjector.Project(Codex(), Handle(), Frame("""{"type":"turn.completed","info":{"total_token_usage":{"input_tokens":900,"output_tokens":120}}}""")).ShouldBeNull();
    }

    /// <summary>
    /// The declared set is unwritable while it contradicts the row. Pinned on the contract as well as in the database,
    /// because the contract check is what keeps a refusal inside the pump instead of taking a batch of frames down.
    /// </summary>
    [Fact]
    public void Declaring_a_figure_unavailable_while_carrying_it_is_refused_by_the_contract()
    {
        var contradiction = Projection() with
        {
            CacheReadTokens = 10,
            UnavailableFigures = new[] { ModelCallFigures.CacheReadTokens },
        };

        contradiction.Validate().ShouldContain(error => error.Contains(ModelCallFigures.CacheReadTokens) && error.Contains("stores a value"));
        Projection().Validate().ShouldBeEmpty();
    }

    [Fact]
    public void A_figure_outside_the_vocabulary_cannot_be_declared()
    {
        var invented = Projection() with { UnavailableFigures = new[] { "effective_provider" } };

        invented.Validate().ShouldContain(error => error.Contains("effective_provider"));
        ModelCallFigures.IsSupported("effective_provider").ShouldBeFalse();
    }

    [Fact]
    public void The_declared_vocabulary_is_exactly_the_seven_names_the_database_admits()
    {
        ModelCallFigures.All.Order(StringComparer.Ordinal).ToArray().ShouldBe(new[]
        {
            "cache_read_tokens", "cache_write_tokens", "completed_at", "cost_amount",
            "first_token_at", "provider_request_id", "reasoning_tokens",
        }, customMessage: "a name added here without 0145's CHECK is refused at insert, and one removed here silently stops being declarable");
    }

    private static CodexHarness Codex() => new();

    /// <summary>An opening of a workflow-bound run — the only kind a model call may be projected from, so it is what every test that expects a projection uses.</summary>
    private static NativeRecordCaptureHandle Handle() => new()
    {
        TeamId = Guid.NewGuid(), AgentRunId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(),
        AttemptId = Guid.NewGuid(), StreamId = Guid.NewGuid(), Channel = NativeRecordChannel.Stdout,
        WorkflowRunId = Guid.NewGuid(),
    };

    private static NativeRecordV1 Frame(string payload) => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion, RecordId = Guid.NewGuid(), StreamId = Guid.NewGuid(),
        Ordinal = 6, Channel = NativeRecordChannel.Stdout, NativeType = "assistant",
        IngestedAt = new DateTimeOffset(2026, 8, 19, 10, 30, 0, TimeSpan.Zero),
        ByteOffset = 1024, ByteLength = Encoding.UTF8.GetByteCount(payload), InlinePayload = payload,
        DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
        Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
        SizeBytes = Encoding.UTF8.GetByteCount(payload), Encoding = NativeRecordPayloadEncoding.Utf8,
        Redaction = NativeRecordRedaction.None, IsFinal = true,
    };

    private static HarnessModelCallProjectionV1 Projection() =>
        GroundedModelCallProjector.Project(Claude, Handle(), Frame(ResponseFrame()))!;

    /// <summary>A harness that hands the projector a record directly, so the projector's own gate is exercised rather than the Claude reader's shape test.</summary>
    private sealed class StatingHarness : IAgentHarness, IAgentModelCallFrameReader
    {
        private readonly GroundedModelCallFrame _stated;

        public StatingHarness(GroundedModelCallFrame stated) => _stated = stated;

        public string Kind => "stating";
        public string Version => "1.0.0";
        public IReadOnlyList<string> Models { get; } = Array.Empty<string>();
        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
        public IAgentEventFolder CreateFolder() => throw new NotSupportedException();
        public GroundedModelCallFrame? ReadModelCallFrame(string nativeFrame) => _stated;
    }
}
