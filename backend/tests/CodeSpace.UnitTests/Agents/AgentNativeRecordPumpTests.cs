using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Reduction;
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

    /// <summary>
    /// The execution identity key a capture opening declares: the adapter's tag plus the generation of the record
    /// contract IT declares, never the major of the native CLI version it pins. The two disagree on purpose — a CLI
    /// major bump leaves an unchanged adapter's rows under one key, and the pinned codex 0.142.x has no leading major
    /// to borrow at all. A generation below the floor is clamped, because the database's own pattern refuses <c>v0</c>
    /// and a number must never be what disables an adapter's capture. Null generation ⇒ the adapter declares none.
    /// </summary>
    [Theory]
    [InlineData("codex-cli", "0.142.2", 1, "codex-cli/v1")]
    [InlineData("codex-cli", "0.142.2", null, "codex-cli/v1")]
    [InlineData("claude-code", "2.1.193", 2, "claude-code/v2")]
    [InlineData("claude-code", "3.0.0", 2, "claude-code/v2")]
    [InlineData("scripted", "9.9.9", 2, "scripted/v2")]
    [InlineData("scripted", "2.0.0", null, "scripted/v1")]
    [InlineData("Scripted", "test", 0, "scripted/v1")]
    [InlineData("scripted", "", -3, "scripted/v1")]
    public void The_harness_execution_key_is_the_adapter_tag_and_its_declared_contract_generation(string kind, string version, int? generation, string expected)
    {
        IAgentHarness harness = generation is { } declared ? new GenerationalHarness(kind, version, declared) : new KeyedHarness(kind, version);

        AgentNativeRecordPump.HarnessTypeKeyOf(harness).ShouldBe(expected,
            customMessage: "the major names the ADAPTER's contract generation, so it must not move with the CLI version string and must not fall below the v1 the database's own key pattern permits");
    }

    /// <summary>
    /// The key each SHIPPED adapter actually emits, pinned to the value a run on its pinned CLI already wrote. A row's
    /// <c>harness_type_key</c> is immutable once written (0137's identity trigger refuses an update to it), so
    /// changing what an unchanged adapter emits splits its history across two keys with no way to repair it — which
    /// is the exact harm this key exists to prevent. Both values are therefore deliberate, not derived.
    /// </summary>
    [Theory]
    [InlineData("codex-cli/v1")]
    [InlineData("claude-code/v2")]
    public void Every_shipped_adapter_keys_its_rows_under_the_generation_it_declares(string expected)
    {
        IAgentHarness harness = expected.StartsWith("codex", StringComparison.Ordinal) ? new CodexHarness() : new ClaudeCodeHarness();

        AgentNativeRecordPump.HarnessTypeKeyOf(harness).ShouldBe(expected,
            customMessage: "rows already written under this key cannot be re-keyed, so emitting a different one splits one adapter's history in two");
    }

    /// <summary>
    /// The re-attach seam, at the position the observation ACTUALLY resumes at. A resumed opening starts its cursor
    /// where the re-attach restarts reading the source rather than at zero, so a frame it records is described at the
    /// position the source really has, and one the pre-restart stream already recorded is recognisable as such instead
    /// of landing on invented ground past that stream's head.
    /// </summary>
    [Fact]
    public async Task A_resumed_opening_records_at_the_position_its_observation_resumes_from()
    {
        // The pre-restart stream recorded "aaa" at 0 and "bb" at 4, so its records reach 7; the last committed offset
        // covered only "aaa", so the re-attach restarts reading at 4 and is re-delivered "bb" before anything new.
        var plane = new RecordingPlane { RecordedHead = 7 };
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Resume(4), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        await pump.CaptureAsync("bb", "bb", new DroppingHarness(), CancellationToken.None);
        await pump.CaptureAsync("ccc", "ccc", new DroppingHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Reopenings.ShouldBe(1, customMessage: "a resumed opening must re-enter the recorded process, never append a second row for the one it is observing");
        plane.Openings.ShouldBe(0);
        plane.Records.Select(record => record.Frame.ByteOffset).ShouldBe(new long[] { 7 },
            customMessage: "the re-delivered line sits below the recorded head and is already a row; only the line past that head is this stream's to record, at the offset the source gives it");
        plane.Records.Select(record => record.Frame.Ordinal).ShouldBe(new long[] { 0 },
            customMessage: "ordinals are per stream and 0139 requires them contiguous, so a dropped line must not consume one");
    }

    /// <summary>
    /// The double count this seam used to ship. A tear-down between a frame's write and the offset that covers it
    /// leaves the records AHEAD of the resume position, so the re-attach is re-delivered lines that already have rows;
    /// recording them again folds the same source line into the reduction twice, over-counting every count and chaining
    /// a digest that witnesses a prefix the process never emitted. Every re-delivered line is dropped, and the first
    /// line past the head is not.
    /// </summary>
    [Fact]
    public async Task A_re_delivered_line_that_already_has_a_record_is_never_recorded_twice()
    {
        // Records cover "one\ntwo\nsix6\n" (0, 4, 8) and so reach 13; the committed offset only reached "one\n".
        var plane = new RecordingPlane { RecordedHead = 13 };
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Resume(4), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        // The executor's own loop: capture the line, then project everything its parser yielded onto that frame.
        foreach (var line in new[] { "two", "six6", "new" })
        {
            var frame = await pump.CaptureAsync(line, line, new EchoHarness(), CancellationToken.None);

            foreach (var normalized in frame.Events) pump.Project(frame, normalized);
        }

        await pump.FlushAsync(CancellationToken.None);

        plane.Records.Select(record => record.Frame.InlinePayload).ShouldBe(new[] { "new" },
            customMessage: "a re-delivered line recorded a second time is the double count: the fold counts records and chains their digests, so the stored state would witness a prefix with a segment the process emitted once and this recorded twice");
        plane.Events.Count.ShouldBe(1, customMessage: "and a projection of a dropped frame would be a projection grounded in nothing, which the plane refuses at the batch");
        plane.Checkpoints.ShouldHaveSingleItem().State.RecordsConsumed.ShouldBe(1,
            customMessage: "the checkpoint claims exactly the frames this opening contributed, not the ones it was merely shown again");
    }

    /// <summary>A re-attach that resumes at or past the recorded head has nothing re-delivered — the ordinary case, and the one where dropping anything would be a lost line rather than a saved duplicate.</summary>
    [Fact]
    public async Task A_resume_at_the_recorded_head_drops_nothing()
    {
        var plane = new RecordingPlane { RecordedHead = 4 };
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Resume(4), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        await pump.CaptureAsync("two", "two", new EchoHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.Select(record => record.Frame.ByteOffset).ShouldBe(new long[] { 4 },
            customMessage: "nothing was re-delivered here, so a drop would be a frame lost — the head is a floor, not a skip count");
    }

    /// <summary>A plane with no live recorded process to resume has nothing this opening could attach frames to, so capture is skipped and the re-attach streams exactly as it did before.</summary>
    [Fact]
    public async Task A_resume_with_no_live_process_leaves_the_parse_path_untouched()
    {
        var plane = new RecordingPlane { RecordedHead = null };
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Resume(0), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        pump.IsCapturing.ShouldBeFalse();
        (await pump.CaptureAsync("hello", "hello", new EchoHarness(), CancellationToken.None)).Events.ShouldHaveSingleItem();
        plane.Records.ShouldBeEmpty();
    }

    /// <summary>
    /// The checkpoint rides the batch's own write, so the stored position can neither lead the frames it claims nor
    /// lag them — the window in which a durable frame is missing from the resumable prefix never opens.
    /// </summary>
    [Fact]
    public async Task A_batch_is_written_together_with_the_checkpoint_of_the_prefix_it_completes()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        pump.IsReducing.ShouldBeTrue();

        await pump.CaptureAsync("one", "one", new EchoHarness(), CancellationToken.None);
        await pump.CaptureAsync("two", "two", new EchoHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.PlainWrites.ShouldBe(0, customMessage: "a batch written apart from its checkpoint is the window this wiring exists to close");
        var checkpoint = plane.Checkpoints.ShouldHaveSingleItem();
        checkpoint.State.RecordsConsumed.ShouldBe(2, customMessage: "the checkpoint claims exactly the frames its own transaction makes durable");
        checkpoint.Position.RecordsConsumed.ShouldBe(2);
        checkpoint.ReducerKind.ShouldBe(HarnessReductionFold.ReducerKind);
    }

    /// <summary>A plane that cannot fold — no reduction capability at all — writes its batches exactly as it did before, with no checkpoint and no change to the frames.</summary>
    [Fact]
    public async Task A_plane_without_the_reduction_capability_writes_its_batches_unchanged()
    {
        var plane = new BatchOnlyPlane();
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Request(), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        pump.IsCapturing.ShouldBeTrue();
        pump.IsReducing.ShouldBeFalse();

        await pump.CaptureAsync("one", "one", new EchoHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.ShouldBe(1, customMessage: "frames are captured whether or not anything folds them");
    }

    /// <summary>
    /// The headline of the grounded projector. A frame the harness recognises as its OWN session record yields a
    /// second projection beside the derived one, claiming exactness — and the fold then takes the NAME from it, which
    /// is the whole reason the exactness distinction exists.
    /// </summary>
    [Fact]
    public async Task A_frame_the_harness_states_its_session_in_is_projected_exactly_and_names_the_fold()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        var frame = await pump.CaptureAsync(SessionNamingHarness.Frame, SessionNamingHarness.Frame, new SessionNamingHarness(), CancellationToken.None);
        foreach (var normalized in frame.Events) pump.Project(frame, normalized);
        await pump.FlushAsync(CancellationToken.None);

        var grounded = plane.Events.Where(candidate => candidate.ProjectionQuality.IsExactlyGrounded()).ShouldHaveSingleItem();

        grounded.ProjectionQuality.ShouldBe(SemanticProjectionQuality.Exact,
            customMessage: "the captured bytes are verbatim, so the strongest honest claim over them is Exact");
        grounded.SessionId.ShouldBe(SessionNamingHarness.Session);
        grounded.SourceNativeRecordIds.ShouldBe(new[] { frame.RecordId!.Value },
            customMessage: "an exact claim with no source frame is a claim about nothing, and both the contract and the database refuse one");
        grounded.Validate().ShouldBeEmpty();

        plane.Events.Count(candidate => candidate.ProjectionQuality == SemanticProjectionQuality.Derived).ShouldBe(1,
            customMessage: "the grounded projection rides BESIDE the normalization of the same frame; replacing it would trade one reading of the frame for another instead of keeping both");

        plane.Checkpoints.ShouldHaveSingleItem().State.FirstSessionId.ShouldBe(SessionNamingHarness.Session,
            customMessage: "the fold takes a named fact only from an exactly grounded projection, so this is the first slice on which a re-attach recovers anything that NAMES something");
    }

    /// <summary>
    /// The rule the quality vocabulary exists for, at the seam that could break it. A harness with no grounded-frame
    /// reader emits frames that MENTION its session all run long, and every projection of them must stay Derived — so
    /// the fold recovers no name at all rather than one it inferred.
    /// </summary>
    [Fact]
    public async Task A_harness_that_states_nothing_grounds_nothing_however_its_lines_read()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        var frame = await pump.CaptureAsync(SessionNamingHarness.Frame, SessionNamingHarness.Frame, new EchoHarness(), CancellationToken.None);
        foreach (var normalized in frame.Events) pump.Project(frame, normalized);
        await pump.FlushAsync(CancellationToken.None);

        plane.Events.ShouldAllBe(candidate => candidate.ProjectionQuality == SemanticProjectionQuality.Derived,
            customMessage: "this line carries a session id in the shape a grounded reader would accept, and a projector that promoted it on those grounds would be pattern-matching prose into an exact claim");
        plane.Checkpoints.ShouldHaveSingleItem().State.FirstSessionId.ShouldBeNull(
            customMessage: "recovering nothing is the correct outcome for a harness that states nothing — ExactlyGroundedProjections is one aggregate over the prefix and could never afterwards say WHICH field had been inferred");
        plane.Checkpoints[0].State.ExactlyGroundedProjections.ShouldBe(0);
    }

    /// <summary>
    /// Exactness is a claim about the bytes that were STORED. A frame whose secret spans were masked is no longer what
    /// the harness wrote, so a fact still readable in it is RedactedExact — which is what that value means, and which
    /// the database independently refuses to let be Exact over a masked source.
    /// </summary>
    [Fact]
    public async Task A_masked_frame_grounds_only_a_redacted_exact_claim()
    {
        const string secret = "sk-live-SENTINEL";
        var plane = new RecordingPlane();
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Request(), new SecretRedactor(new[] { secret }), NullLogger.Instance, CancellationToken.None);

        await pump.CaptureAsync(SessionNamingHarness.FrameWith(secret), SessionNamingHarness.FrameWith(SecretRedactor.Placeholder), new SessionNamingHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        var grounded = plane.Events.Where(candidate => candidate.ProjectionQuality.IsExactlyGrounded()).ShouldHaveSingleItem();

        grounded.ProjectionQuality.ShouldBe(SemanticProjectionQuality.RedactedExact,
            customMessage: "the stored bytes differ from the wire, so Exact would be a claim the evidence cannot support — and the database refuses exactly this pairing, so claiming it here would lose the whole batch");
        grounded.SessionId.ShouldBe(SessionNamingHarness.Session, customMessage: "masking a secret elsewhere in the frame does not stop the harness having stated its session in it");
        plane.Records.ShouldHaveSingleItem().Frame.Redaction.ShouldBe(NativeRecordRedaction.Masked);
    }

    /// <summary>
    /// The all-zero UUID is well-formed and names nothing. <c>Guid.TryParseExact</c> accepts it, so a harness reading a
    /// zeroed session field states it with a straight face; the fold latches the FIRST session it is handed and would
    /// latch that one, leaving a warm resume pointed at a session no reader can open. The refusal is at the projector
    /// and not only in <see cref="GroundedSessionFrame.For"/> because a harness can build the record itself — which is
    /// exactly what this double does.
    /// </summary>
    [Fact]
    public async Task The_zero_session_id_grounds_nothing_even_when_a_harness_states_it()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        await pump.CaptureAsync("zeroed", "zeroed", new ZeroSessionHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.ShouldHaveSingleItem().Frame.InlinePayload.ShouldBe("zeroed", customMessage: "refusing the id must not cost the frame — the record is the evidence either way");
        plane.Events.ShouldBeEmpty(customMessage: "an id that names nothing is not a fact, and one projected as exactly grounded can never afterwards be told from one that is");
        plane.Checkpoints.ShouldHaveSingleItem().State.FirstSessionId.ShouldBeNull(
            customMessage: "null is 'no session was named'; Guid.Empty latched here would be 'a session was named' and would answer a warm resume with an id nothing can resume");
    }

    /// <summary>
    /// The containment asymmetry, stated in a test because it is easy to get backwards. A PARSER that throws still
    /// fails the run — it did before this plane existed and must keep doing so. A GROUNDED READER that throws is new
    /// work, so it costs the run nothing: the frame is still recorded, and the run resolves as it does with no plane.
    /// </summary>
    [Fact]
    public async Task A_grounded_reader_that_throws_still_records_the_frame_and_costs_the_run_nothing()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        var frame = await pump.CaptureAsync("hello", "hello", new ThrowingGroundedHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        frame.Events.ShouldHaveSingleItem(customMessage: "the parse is untouched by a grounded reader that failed before it");
        plane.Records.ShouldHaveSingleItem().Frame.InlinePayload.ShouldBe("hello");
        plane.Events.ShouldBeEmpty(customMessage: "no fact was read, so no projection may claim one — and no exception may reach the run, or a shadow plane has decided its outcome");
    }

    /// <summary>
    /// The recovery this lane exists for, at the tier that can execute it: a worker records the frame its harness named
    /// its session in, is replaced, and the reduction the replacement lands on still carries the name. The tail-only
    /// fold is asserted NOT to know it, or a resume that recovered nothing would pass this test.
    /// </summary>
    [Fact]
    public async Task A_session_named_before_a_worker_replacement_survives_the_resumed_fold()
    {
        var plane = new RecordingPlane();
        var harness = new SessionNamingHarness();
        var before = await OpenAsync(plane);

        await before.CaptureAsync(SessionNamingHarness.Frame, SessionNamingHarness.Frame, harness, CancellationToken.None);
        await before.FlushAsync(CancellationToken.None);

        plane.Checkpoints.ShouldHaveSingleItem().State.FirstSessionId.ShouldBe(SessionNamingHarness.Session);

        // The replacement re-enters the SAME process at the head its records already reach, and resumes the execution's
        // stored reduction — the two halves a re-attach has.
        var after = await AgentNativeRecordPump.OpenAsync(plane, Resume(SessionNamingHarness.Frame.Length + 1), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        await after.CaptureAsync("tail", "tail", harness, CancellationToken.None);
        await after.FlushAsync(CancellationToken.None);

        plane.Checkpoints[^1].State.FirstSessionId.ShouldBe(SessionNamingHarness.Session,
            customMessage: "the session was named ONCE, before the replacement — a fold that started from nothing would answer null here, which is exactly the defect this spine exists to end");
        plane.Checkpoints[^1].State.RecordsConsumed.ShouldBe(2);
    }

    /// <summary>
    /// Stderr's own rule. A diagnostic must never reach a parser written for the harness's stdout protocol: a warning
    /// shaped like a protocol frame would be normalized into an event the harness never emitted. The record still
    /// lands, and it says NO parse was attempted — which is a different fact from a parse that found nothing.
    /// </summary>
    [Fact]
    public async Task A_diagnostic_line_is_recorded_with_no_parser_ever_asked_about_it()
    {
        var plane = new RecordingPlane();
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Resume(0) with { Channel = NativeRecordChannel.Stderr }, SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        await pump.CaptureDiagnosticAsync("warn: retrying", "warn: retrying", isComplete: true, CancellationToken.None);
        await pump.CaptureDiagnosticAsync("{\"type\":\"assistant\"}", "{\"type\":\"assistant\"}", isComplete: true, CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.Select(record => record.Normalization).ShouldAllBe(normalization => normalization == NativeRecordNormalization.NotParsed,
            customMessage: "Unrecognized would assert that a parser looked and found nothing; no parser was asked, and the difference is exactly what keeps 'which frames could we not interpret' answerable");
        plane.Records.Select(record => record.Frame.Channel).ShouldAllBe(channel => channel == NativeRecordChannel.Stderr);
        plane.Events.ShouldBeEmpty(
            customMessage: "the second line is a protocol frame by shape; projecting it would put into the semantic stream an assistant turn that only ever appeared in a diagnostic");

        plane.Records.Select(record => record.Frame.Ordinal).ShouldBe(new long[] { 0, 1 });
        plane.Records.Select(record => record.Frame.ByteOffset).ShouldBe(new long[] { 0, 15 },
            customMessage: "stderr carries its own contiguous source geometry — the cursor advances by the raw line plus the terminator the stream carried, exactly as stdout's does");
    }

    /// <summary>
    /// Retention on the diagnostic path, at the PUMP: many diagnostic frames must flush like stdout's do rather than
    /// accumulate into a third of the unbounded shapes #1479 and #1489 each removed once. What bounds the SOURCE — how
    /// many lines are ever handed to this pump — is the drain's own budget, pinned a tier down at
    /// <c>LocalProcessDurableRunnerTests</c>, because this test's caller is the test itself.
    /// </summary>
    [Fact]
    public async Task Many_diagnostic_frames_never_retain_more_than_the_buffer_cap()
    {
        const int lines = 5_000;
        var plane = new RecordingPlane();
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Resume(0) with { Channel = NativeRecordChannel.Stderr }, SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        for (var index = 0; index < lines; index++)
            await pump.CaptureDiagnosticAsync($"warn {index}", $"warn {index}", isComplete: true, CancellationToken.None);

        await pump.FlushAsync(CancellationToken.None);

        plane.LargestRecordBatch.ShouldBeLessThanOrEqualTo(AgentNativeRecordPump.MaxBuffered,
            customMessage: $"the diagnostic path retained {plane.LargestRecordBatch} frames in one batch against a cap of {AgentNativeRecordPump.MaxBuffered} — routing stderr through the pump must not reintroduce the unbounded accumulation the pump removed for stdout");
        plane.Records.Count.ShouldBe(lines, customMessage: "bounding retention must not cost a diagnostic line — a harness's own error output is the thing this makes durable");
        plane.Batches.ShouldBeGreaterThan(1, customMessage: "a stream far longer than the cap must have been flushed more than once, or the cap never fired at all");
    }

    /// <summary>
    /// What a diagnostic the reader had to CUT becomes. A line longer than the reader's own pass is delivered in
    /// pieces so the drain can get past it at all, and the piece is recorded as the partial it is: a reader of this
    /// stream that stops on the first record must be able to see that it holds half a frame. Two final records would
    /// assert two diagnostics where the harness wrote one.
    ///
    /// <para>And the cursor stays TRUE across the cut. A cut line carried no terminator byte, so none is counted for
    /// it — count one and the continuation's own record claims a byte range starting one past where the reader
    /// resumed, which is how the recorded head and the resume position stop being comparable at all.</para>
    /// </summary>
    [Fact]
    public async Task A_diagnostic_the_reader_cut_is_recorded_as_a_partial_and_costs_the_cursor_no_terminator()
    {
        var plane = new RecordingPlane();
        var pump = await AgentNativeRecordPump.OpenAsync(plane, Resume(0) with { Channel = NativeRecordChannel.Stderr }, SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

        await pump.CaptureDiagnosticAsync("half a stack", "half a stack", isComplete: false, CancellationToken.None);
        await pump.CaptureDiagnosticAsync(" trace", " trace", isComplete: true, CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.Select(record => record.Frame.IsFinal).ShouldBe(new[] { false, true },
            customMessage: "a cut recorded as final is a durable claim that the harness emitted a diagnostic ending mid-word, and nothing downstream could afterwards tell it from one that did");
        plane.Records.Select(record => record.Frame.ByteOffset).ShouldBe(new long[] { 0, 12 },
            customMessage: "the cut consumed 12 source bytes and no terminator, so its continuation begins at 12 — counting a terminator the stream never carried drifts every later frame's geometry off the source");
    }

    /// <summary>
    /// The model-call half of the grounded read, on the pump: a frame the harness records a call in rides the SAME batch
    /// as the frame itself and the event that cites it, so a cost row can never be durable while its only evidence is
    /// not — and it is buffered, never written per line.
    /// </summary>
    [Fact]
    public async Task A_frame_that_records_a_model_call_rides_the_frames_own_batch()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        await pump.CaptureAsync(ModelCallingHarness.Frame, ModelCallingHarness.Frame, new ModelCallingHarness(), CancellationToken.None);

        plane.Batches.ShouldBe(0, customMessage: "a per-line round trip would put a database write in the middle of the harness's output loop");

        await pump.FlushAsync(CancellationToken.None);

        var batch = plane.ModelCalls.ShouldHaveSingleItem();
        var record = plane.Records.ShouldHaveSingleItem();

        batch.SourceNativeRecordId.ShouldBe(record.Frame.RecordId, customMessage: "the frame is the row's only evidence, so the two are one write or the row cites nothing");
        batch.Model.ShouldBe(ModelCallingHarness.Model);
        batch.Validate().ShouldBeEmpty();
        plane.Events.ShouldHaveSingleItem().ModelCallId.ShouldBe(batch.ModelCallId,
            customMessage: "the exactly grounded event is what joins the frame to the call row");
    }

    /// <summary>A harness that records no model call contributes none, so the write is byte-identical to one where this plane does not exist.</summary>
    [Fact]
    public async Task A_harness_that_records_no_model_call_leaves_the_batch_without_one()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        await pump.CaptureAsync(ModelCallingHarness.Frame, ModelCallingHarness.Frame, new EchoHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.Records.ShouldHaveSingleItem();
        plane.ModelCalls.ShouldBeEmpty("a harness with no model-call reader must contribute nothing, however much a frame looks like a response");
    }

    /// <summary>
    /// Retention: the buffered calls do not survive their flush, exactly as the records and events do not. A run that
    /// makes thousands of calls must not accumulate one row per call in managed memory.
    /// </summary>
    [Fact]
    public async Task A_flushed_model_call_does_not_survive_into_the_next_batch()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);
        var harness = new ModelCallingHarness();

        await pump.CaptureAsync(ModelCallingHarness.Frame, ModelCallingHarness.Frame, harness, CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);
        await pump.CaptureAsync(ModelCallingHarness.FrameWith("msg_02"), ModelCallingHarness.FrameWith("msg_02"), harness, CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        plane.LargestModelCallBatch.ShouldBe(1, customMessage: "a call re-sent with the next batch would be a second billed call for one response");
        plane.ModelCalls.Count.ShouldBe(2);
    }

    /// <summary>
    /// The containment asymmetry again, for the second grounded reader: a model-call reader that throws costs the run
    /// nothing AND does not cost the frame its session fact, because each reader is contained on its own.
    /// </summary>
    [Fact]
    public async Task A_model_call_reader_that_throws_keeps_the_frames_other_grounded_fact()
    {
        var plane = new RecordingPlane();
        var pump = await OpenAsync(plane);

        var frame = await pump.CaptureAsync(SessionNamingHarness.Frame, SessionNamingHarness.Frame, new ThrowingModelCallHarness(), CancellationToken.None);
        await pump.FlushAsync(CancellationToken.None);

        frame.Events.ShouldHaveSingleItem(customMessage: "the parse is untouched by a grounded reader that failed before it");
        plane.ModelCalls.ShouldBeEmpty("no call was read, so no row may claim one");
        plane.Events.ShouldHaveSingleItem().SessionId.ShouldBe(SessionNamingHarness.Session,
            customMessage: "one grounded reader failing must not cost the frame a fact another reader DID state — they are contained separately for exactly this");
    }

    private static Task<AgentNativeRecordPump> OpenAsync(INativeRecordPlane plane) =>
        AgentNativeRecordPump.OpenAsync(plane, Request(), SecretRedactor.None, NullLogger.Instance, CancellationToken.None);

    /// <summary>A re-attach's request: resuming, and carrying the source position its observation is about to restart reading at — which is what the executor hands over from the runner handle.</summary>
    private static NativeRecordCaptureRequest Resume(long resumeSourceOffset) => Request() with { Resume = true, ResumeSourceOffset = resumeSourceOffset };

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

    /// <summary>
    /// Accepts every batch and remembers the HIGH-WATER mark of each — which is what a retention claim is actually
    /// about, rather than the totals. It also carries both sibling capabilities, because the deployed plane does: a
    /// double that knew only the batch writer could never show that a checkpoint rides its batch's own write.
    /// </summary>
    private sealed class RecordingPlane : INativeRecordPlane, INativeRecordExecutionPlane, INativeRecordReductionPlane
    {
        private readonly Guid _executionId = Guid.NewGuid();
        private readonly Guid _workflowRunId = Guid.NewGuid();

        public List<NativeRecordCapture> Records { get; } = new();
        public List<AgentSemanticEventV1> Events { get; } = new();
        public List<HarnessModelCallProjectionV1> ModelCalls { get; } = new();
        public List<HarnessReductionCheckpointV1> Checkpoints { get; } = new();
        public int LargestRecordBatch { get; private set; }
        public int LargestEventBatch { get; private set; }
        public int LargestModelCallBatch { get; private set; }
        public int Batches { get; private set; }
        public int PlainWrites { get; private set; }
        public int Openings { get; private set; }
        public int Reopenings { get; private set; }
        public int Terminalizations { get; private set; }

        /// <summary>How far this process's frames already reach, or null for a run with no live recorded process to resume at all. The cursor a resumed opening starts at comes from the REQUEST, exactly as the deployed plane takes it.</summary>
        public long? RecordedHead { get; init; } = 0;

        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken)
        {
            Openings++;

            return Task.FromResult<NativeRecordCaptureHandle?>(Handle(request.TeamId, request.AgentRunId, request.Channel));
        }

        public Task<NativeRecordCaptureOpening?> ReopenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken)
        {
            Reopenings++;

            return Task.FromResult(RecordedHead is not { } head
                ? null
                : new NativeRecordCaptureOpening
                {
                    Handle = Handle(request.TeamId, request.AgentRunId, request.Channel),
                    SourceHead = request.ResumeSourceOffset,
                    RecordedHead = head,
                });
        }

        public Task TerminalizeAsync(Guid teamId, Guid agentRunId, long expectedEpoch, CancellationToken cancellationToken)
        {
            Terminalizations++;

            return Task.CompletedTask;
        }

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
        {
            PlainWrites++;

            return Accept(batch);
        }

        /// <summary>The stored reduction, as the deployed plane keeps it: whatever the last accepted write carried. A second opening therefore RESUMES the first one's fold, which is the only way a test can show a fact surviving a worker replacement.</summary>
        public Task<HarnessReductionCheckpointV1?> ReadCheckpointAsync(Guid teamId, Guid executionId, string reducerKind, CancellationToken cancellationToken) =>
            Task.FromResult(Checkpoints.LastOrDefault(checkpoint => checkpoint.ExecutionId == executionId && checkpoint.ReducerKind == reducerKind));

        public Task WriteReducedAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken)
        {
            Checkpoints.Add(checkpoint);

            return Accept(batch);
        }

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => Task.CompletedTask;

        private NativeRecordCaptureHandle Handle(Guid teamId, Guid agentRunId, NativeRecordChannel channel) => new()
        {
            TeamId = teamId, AgentRunId = agentRunId, ExecutionId = _executionId,
            AttemptId = Guid.NewGuid(), StreamId = Guid.NewGuid(), Channel = channel,
            WorkflowRunId = _workflowRunId,
        };

        private Task Accept(NativeRecordBatch batch)
        {
            Batches++;
            LargestRecordBatch = Math.Max(LargestRecordBatch, batch.Records.Count);
            LargestEventBatch = Math.Max(LargestEventBatch, batch.Events.Count);
            LargestModelCallBatch = Math.Max(LargestModelCallBatch, batch.ModelCalls.Count);
            Records.AddRange(batch.Records);
            Events.AddRange(batch.Events);
            ModelCalls.AddRange(batch.ModelCalls);

            return Task.CompletedTask;
        }
    }

    /// <summary>The shape of a deployment (or a hand-built double) that knows only the batch writer — no resume, no reduction.</summary>
    private sealed class BatchOnlyPlane : INativeRecordPlane
    {
        public int Records { get; private set; }

        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<NativeRecordCaptureHandle?>(new NativeRecordCaptureHandle
            {
                TeamId = request.TeamId, AgentRunId = request.AgentRunId, ExecutionId = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(), StreamId = Guid.NewGuid(), Channel = request.Channel,
                WorkflowRunId = Guid.NewGuid(),
            });

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
        {
            Records += batch.Records.Count;

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
                WorkflowRunId = Guid.NewGuid(),
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

    /// <summary>
    /// A harness that STATES its session in one of its own structured frames — the shape Claude Code's
    /// <c>system</c>/<c>init</c> line and Codex's <c>thread.started</c> line both have. Its reader answers only for
    /// that frame, exactly as the real ones do.
    /// </summary>
    private sealed class SessionNamingHarness : StubHarness, IAgentGroundedFrameReader
    {
        internal static readonly Guid Session = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

        internal static string Frame => FrameWith("none");

        internal static string FrameWith(string apiKey) => $"{{\"type\":\"session\",\"session_id\":\"{Session:D}\",\"api_key\":\"{apiKey}\"}}";

        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            new[] { new AgentEvent { Kind = AgentEventKind.Started, Text = "Session started" } };

        public GroundedSessionFrame? ReadSessionFrame(string nativeFrame) =>
            nativeFrame.Contains($"\"session_id\":\"{Session:D}\"", StringComparison.Ordinal) ? new GroundedSessionFrame { SessionId = Session } : null;
    }

    /// <summary>A grounded reader that fails. New work must never be able to fail a run that would otherwise succeed.</summary>
    private sealed class ThrowingGroundedHarness : StubHarness, IAgentGroundedFrameReader
    {
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine } };

        public GroundedSessionFrame? ReadSessionFrame(string nativeFrame) => throw new InvalidOperationException("the grounded reader could not read this frame");
    }

    /// <summary>A harness that STATES the all-zero session, built by hand so it bypasses <see cref="GroundedSessionFrame.For"/> — the shape a third-party adapter reading a zeroed field reaches without meaning to.</summary>
    private sealed class ZeroSessionHarness : StubHarness, IAgentGroundedFrameReader
    {
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();

        public GroundedSessionFrame? ReadSessionFrame(string nativeFrame) => new() { SessionId = Guid.Empty };
    }

    /// <summary>
    /// A harness that RECORDS one model call per response frame — the shape Claude Code's <c>assistant</c> envelope has,
    /// whose nested message is the provider's own response object. Its reader answers only for that frame.
    /// </summary>
    private sealed class ModelCallingHarness : StubHarness, IAgentModelCallFrameReader
    {
        internal const string Model = "test-model";

        internal static string Frame => FrameWith("msg_01");

        /// <summary>A real provider-response envelope, so a wiring that consulted SOME OTHER harness's reader instead of this one would still find a call here — which is what makes the "a harness with no reader contributes nothing" test falsifiable.</summary>
        internal static string FrameWith(string callId) =>
            $"{{\"type\":\"assistant\",\"message\":{{\"id\":\"{callId}\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"{Model}\",\"usage\":{{\"input_tokens\":12,\"output_tokens\":3}}}}}}";

        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine } };

        public GroundedModelCallFrame? ReadModelCallFrame(string nativeFrame) =>
            nativeFrame.Contains("\"id\":\"msg_", StringComparison.Ordinal)
                ? new GroundedModelCallFrame { CallId = nativeFrame, Model = Model, InputTokens = 12, OutputTokens = 3 }
                : null;
    }

    /// <summary>A model-call reader that fails, on a frame whose SESSION the same harness does state — so the test can show one reader's failure does not cost the other's fact.</summary>
    private sealed class ThrowingModelCallHarness : StubHarness, IAgentGroundedFrameReader, IAgentModelCallFrameReader
    {
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine } };

        public GroundedSessionFrame? ReadSessionFrame(string nativeFrame) =>
            nativeFrame.Contains($"\"session_id\":\"{SessionNamingHarness.Session:D}\"", StringComparison.Ordinal)
                ? new GroundedSessionFrame { SessionId = SessionNamingHarness.Session }
                : null;

        public GroundedModelCallFrame? ReadModelCallFrame(string nativeFrame) => throw new InvalidOperationException("the model-call reader could not read this frame");
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

    /// <summary>A <see cref="KeyedHarness"/> that also DECLARES its record-contract generation — the shape every shipped adapter has, and the only way the key can disagree with the CLI version string.</summary>
    private sealed class GenerationalHarness : StubHarness, IAgentHarnessContractGeneration
    {
        private readonly string _kind;
        private readonly string _version;

        public GenerationalHarness(string kind, string version, int contractGeneration) { _kind = kind; _version = version; ContractGeneration = contractGeneration; }

        public override string Kind => _kind;
        public override string Version => _version;
        public int ContractGeneration { get; }
        public override IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();
    }
}
