using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The capture pump's own contract, which is where losslessness is either true or not. The normalized event log has no
/// row for a native frame class the harness parser never learned, so the only thing standing between "we dropped it"
/// and "we recorded it and could not read it" is that this pump records the frame BEFORE it asks the parser anything —
/// and keeps recording it when the parser drops the line or throws on it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentNativeRecordPumpTests
{
    /// <summary>
    /// The losslessness pin. A line the parser drops is exactly the case the normalized log cannot represent, so the
    /// record must exist and must say so — Unrecognized, with no error, cited by nothing.
    /// </summary>
    [Fact]
    public async Task A_line_the_parser_drops_still_produces_its_native_record()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        var frame = await pump.CaptureAsync("{\"type\":\"token_count\",\"total\":12}", "{\"type\":\"token_count\",\"total\":12}", new DroppingHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        frame.Events.ShouldBeEmpty();
        frame.RecordId.ShouldNotBeNull("the frame is captured whatever the parser then makes of it");

        var captured = plane.Records.ShouldHaveSingleItem();
        captured.Normalization.ShouldBe(NativeRecordNormalization.Unrecognized,
            customMessage: "an unrecognised frame is the silent drop this plane exists to make durable — recording it as Projected would hide exactly the defect");
        captured.NormalizationErrorCode.ShouldBeNull("nothing failed; the parser simply had nothing to say");
        captured.Frame.NativeType.ShouldBe("token_count",
            customMessage: "without the frame's own type name, 'which native classes are we failing to interpret' stays unanswerable");
        plane.Events.ShouldBeEmpty();
    }

    /// <summary>
    /// A parser that throws must lose its interpretation of the frame, not the frame — AND must re-raise, so the run
    /// resolves exactly as it did before this plane existed. The record is flushed on the way out, because a throw
    /// unwinds the round and the buffered batch gets no later chance to become durable.
    /// </summary>
    [Fact]
    public async Task A_parser_that_throws_records_its_frame_durably_and_re_raises()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => pump.CaptureAsync("boom", "boom", new ThrowingHarness(), CancellationToken.None));

        thrown.Message.ShouldBe(ThrowingHarness.Reason,
            customMessage: "capture must hand the run the PARSER's failure, not one of its own — swallowing it would turn a run that used to fail into one that succeeds");

        var captured = plane.Records.ShouldHaveSingleItem();
        captured.Normalization.ShouldBe(NativeRecordNormalization.Failed);
        captured.NormalizationErrorCode.ShouldBe(AgentNativeRecordPump.NormalizationThrewErrorCode);
        captured.NormalizationErrorMessage.ShouldBe(ThrowingHarness.Reason,
            customMessage: "the reason is the whole value of the marker — a Failed record nobody can diagnose is a hole with a label on it");
        captured.Frame.InlinePayload.ShouldBe("boom", customMessage: "the payload is captured before the parser is consulted, so a throw cannot reach it");
    }

    /// <summary>
    /// The redaction hole. Every other durable string here is derived from the already-redacted bytes; a parser's own
    /// exception message is not, and a harness that quotes the line it choked on would put the run's key in a column
    /// sitting next to the claim that no secret reaches storage.
    /// </summary>
    [Fact]
    public async Task A_parsers_exception_message_is_redacted_before_it_is_recorded()
    {
        const string secret = "sk-live-SENTINEL";
        var plane = new RecordingPlane();
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Request(), new SecretRedactor(new[] { secret }), NullLogger.Instance, CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(() => pump.CaptureAsync($"key={secret}", "key=***", new EchoingThrowHarness(), CancellationToken.None));

        var message = plane.Records.ShouldHaveSingleItem().NormalizationErrorMessage!;

        message.ShouldNotContain(secret,
            customMessage: "the parser's message is the ONE string on a record not derived from the redacted bytes, and it lands in normalization_error_message verbatim unless it is redacted here");
        message.ShouldContain(SecretRedactor.Placeholder);
    }

    /// <summary>A parsed line's events must cite the frame they came from, and must claim no more fidelity than a normalization can support.</summary>
    [Fact]
    public async Task A_projected_event_cites_its_source_frame_and_claims_only_a_derived_fidelity()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        var frame = await pump.CaptureAsync("hello", "hello", new EchoHarness(), CancellationToken.None);
        foreach (var normalized in frame.Events) pump.Project(frame, normalized);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.ShouldHaveSingleItem().Normalization.ShouldBe(NativeRecordNormalization.Projected);

        var projection = plane.Events.ShouldHaveSingleItem();
        projection.SourceNativeRecordIds.ShouldBe(new[] { frame.RecordId!.Value },
            customMessage: "a projection that names no frame is a claim about nothing, and the database refuses one");
        projection.EventType.ShouldBe("https://codespace.dev/agent/v1/assistant-message");
        projection.ProjectionQuality.ShouldBe(SemanticProjectionQuality.Derived);
        projection.ProjectionQuality.IsExactlyGrounded().ShouldBeFalse(
            customMessage: "ParseEvents normalizes a frame rather than transcribing it, and a projection that claimed the harness's own words for a normalization is how a guessed fact gets audited as a stated one");
        projection.Necessity.ShouldBe(SemanticEventNecessity.Ignorable);
        projection.Validate().ShouldBeEmpty();
    }

    /// <summary>
    /// The secret/lossless tension, resolved rather than picked: the payload is the REDACTED frame, while the source
    /// geometry describes the RAW one — so how much redaction changed is computable, and a masked frame can never be
    /// read back as verbatim.
    /// </summary>
    [Theory]
    [InlineData("plain line", "plain line", NativeRecordRedaction.None)]
    [InlineData("key=sk-live-SENTINEL", "key=***", NativeRecordRedaction.Masked)]
    public async Task A_captured_frame_binds_the_redacted_bytes_and_the_raw_geometry(string rawLine, string redactedLine, NativeRecordRedaction expected)
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        await pump.CaptureAsync(rawLine, redactedLine, new DroppingHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        var frame = plane.Records.ShouldHaveSingleItem().Frame;

        frame.Redaction.ShouldBe(expected);
        frame.InlinePayload.ShouldBe(redactedLine, customMessage: "the durable payload is the redacted stream — an unredacted secret must never reach storage");
        frame.SizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(redactedLine));
        frame.Digest.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(redactedLine))).ToLowerInvariant(),
            customMessage: "the digest binds the CAPTURED bytes; binding the raw ones would make every masked frame unverifiable");
        frame.ByteLength.ShouldBe(Encoding.UTF8.GetByteCount(rawLine),
            customMessage: "the raw length is what makes 'how much did redaction change' a computable fact instead of a lost one");
        frame.Validate().ShouldBeEmpty(customMessage: "a frame the writer builds must satisfy the contract it is written against, or the plane and the type have already drifted");
    }

    /// <summary>Ordinals are contiguous from zero and the source cursor advances by the raw frame plus its terminator, so a stream can be read back in order without consulting anything else.</summary>
    [Fact]
    public async Task Frames_are_numbered_contiguously_from_zero_along_an_advancing_source_cursor()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);
        var harness = new DroppingHarness();

        await pump.CaptureAsync("aaa", "aaa", harness, CancellationToken.None);
        await pump.CaptureAsync("bb", "bb", harness, CancellationToken.None);
        await pump.CaptureAsync("c", "c", harness, CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.Select(record => record.Frame.Ordinal).ShouldBe(new long[] { 0, 1, 2 });
        plane.Records.Select(record => record.Frame.ByteOffset).ShouldBe(new long[] { 0, 4, 7 },
            customMessage: "the cursor advances by the raw frame plus the terminator the stream carried");
        plane.Records.Select(record => record.Frame.StreamId).Distinct().Count().ShouldBe(1,
            customMessage: "one opening is one stream, or the ordinals of two openings collide in the same sequence");
    }

    /// <summary>
    /// Retention. #1479 and #1489 removed the two accumulators that could exhaust a long run's heap; this must not be
    /// the third. The pump's high-water mark is what proves it: with the cap in place no batch can exceed it however
    /// long the stream, and every frame still arrives.
    /// </summary>
    [Fact]
    public async Task Folding_a_large_stream_never_retains_more_than_the_buffer_cap()
    {
        const int lines = 5_000;
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);
        var harness = new EchoHarness();

        for (var index = 0; index < lines; index++)
        {
            var frame = await pump.CaptureAsync($"line {index}", $"line {index}", harness, CancellationToken.None);

            foreach (var normalized in frame.Events) pump.Project(frame, normalized);
        }

        await pump.FlushAsync(CancellationToken.None);

        plane.LargestRecordBatch.ShouldBeLessThanOrEqualTo(AgentNativeRecordPump.MaxBuffered,
            customMessage: $"the pump retained {plane.LargestRecordBatch} frames in one batch against a cap of {AgentNativeRecordPump.MaxBuffered} — retention is growing with the event count, which is the failure #1479 and #1489 removed twice already");
        plane.LargestEventBatch.ShouldBeLessThanOrEqualTo(AgentNativeRecordPump.MaxBuffered);
        plane.Records.Count.ShouldBe(lines, customMessage: "bounding retention must not cost a single frame — that trade is the lossiness this plane exists to end");
        plane.Events.Count.ShouldBe(lines);
        plane.Batches.ShouldBeGreaterThan(1, customMessage: "a stream far longer than the cap must have been flushed more than once, or the cap never fired at all");
    }

    /// <summary>A plane that will not open, or will not accept a batch, must cost the run nothing but a log line — the harness's own output path is not allowed to depend on a shadow plane.</summary>
    [Fact]
    public async Task A_plane_that_refuses_to_open_or_to_write_leaves_the_parse_path_untouched()
    {
        var refusedOpen = await AgentNativeRecordPump.OpenAsync(new ThrowingPlane(), Request(), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        refusedOpen.IsCapturing.ShouldBeFalse();
        (await refusedOpen.CaptureAsync("hello", "hello", new EchoHarness(), CancellationToken.None)).Events.ShouldHaveSingleItem();

        // The half a plane-less deployment is actually about: with no record to carry the reason, a contained throw
        // would erase the failure entirely and resolve the run Succeeded where it used to resolve Failed.
        await Should.ThrowAsync<InvalidOperationException>(() => refusedOpen.CaptureAsync("boom", "boom", new ThrowingHarness(), CancellationToken.None));

        var refusedWrite = new RefusingWritePlane();
        var pump = await OpenAsync(refusedWrite);

        await pump.CaptureAsync("hello", "hello", new EchoHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        refusedWrite.Attempts.ShouldBe(1);

        await pump.CaptureAsync("again", "again", new EchoHarness(), CancellationToken.None);
        await pump.CloseAsync(0, CancellationToken.None);

        refusedWrite.Attempts.ShouldBe(1, customMessage: "a plane that has already failed must not be re-tried once per line for the rest of the run");
        refusedWrite.Closed.ShouldBeFalse();
    }

    /// <summary>The execution identity key a capture opening declares. The database's own pattern refuses anything else, so an adapter whose version names no number must still produce a legal key rather than disabling capture.</summary>
    [Theory]
    [InlineData("codex-cli", "2.1.0", "codex-cli/v2")]
    [InlineData("claude-code", "1.0.60", "claude-code/v1")]
    [InlineData("Scripted", "test", "scripted/v1")]
    [InlineData("scripted", "", "scripted/v1")]
    public void The_harness_execution_key_is_the_adapter_tag_and_its_pinned_major(string kind, string version, string expected)
    {
        AgentNativeRecordPump.HarnessTypeKeyOf(new KeyedHarness(kind, version)).ShouldBe(expected);
    }

    private static Task<AgentNativeRecordPump> OpenAsync(INativeRecordPlane plane) =>
        AgentNativeRecordPump.OpenAsync(plane, Request(), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

    private static NativeRecordCaptureRequest Request() => new()
    {
        TeamId = Guid.NewGuid(),
        AgentRunId = Guid.NewGuid(),
        HarnessTypeKey = "scripted/v1",
        RunnerKind = "local",
        RunnerLocatorJson = "{\"spoolKey\":\"round-0\"}",
        WorkerFenceEpoch = 3,
        Channel = NativeRecordChannel.Stdout,
    };

    /// <summary>Accepts every batch and remembers the HIGH-WATER mark of each — which is what a retention claim is actually about, rather than the totals.</summary>
    private sealed class RecordingPlane : INativeRecordPlane
    {
        public List<NativeRecordCapture> Records { get; } = new();
        public List<AgentSemanticEventV1> Events { get; } = new();
        public int LargestRecordBatch { get; private set; }
        public int LargestEventBatch { get; private set; }
        public int Batches { get; private set; }

        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<NativeRecordCaptureHandle?>(new NativeRecordCaptureHandle
            {
                TeamId = request.TeamId, AgentRunId = request.AgentRunId, ExecutionId = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(), StreamId = Guid.NewGuid(), Channel = request.Channel,
            });

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
        {
            Batches++;
            LargestRecordBatch = Math.Max(LargestRecordBatch, batch.Records.Count);
            LargestEventBatch = Math.Max(LargestEventBatch, batch.Events.Count);
            Records.AddRange(batch.Records);
            Events.AddRange(batch.Events);

            return Task.CompletedTask;
        }

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingPlane : INativeRecordPlane
    {
        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the execution identity could not be opened");

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RefusingWritePlane : INativeRecordPlane
    {
        public int Attempts { get; private set; }
        public bool Closed { get; private set; }

        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<NativeRecordCaptureHandle?>(new NativeRecordCaptureHandle
            {
                TeamId = request.TeamId, AgentRunId = request.AgentRunId, ExecutionId = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(), StreamId = Guid.NewGuid(), Channel = request.Channel,
            });

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("the batch could not be persisted");
        }

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken)
        {
            Closed = true;
            return Task.CompletedTask;
        }
    }

    private abstract class StubHarness : IAgentHarness
    {
        public virtual string Kind => "scripted";
        public virtual string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };
        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/true" };
        public abstract IReadOnlyList<AgentEvent> ParseEvents(string rawLine);
        public IAgentEventFolder CreateFolder() => throw new NotSupportedException("the pump never folds");
    }

    /// <summary>The harness whose parser does not recognise the frame — the case the normalized log has no row for at all.</summary>
    private sealed class DroppingHarness : StubHarness
    {
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
    }

    private sealed class ThrowingHarness : StubHarness
    {
        internal const string Reason = "the native frame could not be normalized";

        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new InvalidOperationException(Reason);
    }

    /// <summary>The realistic shape of the redaction hole: a parser that quotes the frame it could not read straight into its exception message.</summary>
    private sealed class EchoingThrowHarness : StubHarness
    {
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new InvalidOperationException($"could not read native frame: {rawLine}");
    }

    private sealed class EchoHarness : StubHarness
    {
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine } };
    }

    private sealed class KeyedHarness : StubHarness
    {
        private readonly string _kind;
        private readonly string _version;

        public KeyedHarness(string kind, string version) { _kind = kind; _version = version; }

        public override string Kind => _kind;
        public override string Version => _version;
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
    }
}
