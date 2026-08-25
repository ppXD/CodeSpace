using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Llm;

/// <summary>
/// The pure side-channel recorder over the LLM client seam: a structured model call lands a correlated
/// <c>interaction.started</c> + <c>interaction.completed</c> (or <c>interaction.failed</c>) on the ledger, carrying the
/// open <c>kind</c> + prompt + usage off the ambient <see cref="LlmCallContext"/> — and NEVER alters or faults the
/// model call (verbatim pass-through; fail-open on a capture write error; records nothing with no scope pushed).
/// </summary>
[Trait("Category", "Unit")]
public class RecordingLLMClientDecoratorTests
{
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly Guid Team = Guid.NewGuid();

    private static StructuredLLMCompletionRequest Request() => new()
    {
        Model = "claude-x",
        SystemPrompt = "SYS",
        UserPrompt = "USR",
        JsonSchema = JsonDocument.Parse("{}").RootElement,
    };

    private static StructuredLLMCompletion Completion() => new()
    {
        Json = JsonSerializer.SerializeToElement(new { kind = "plan" }),
        Model = "claude-x",
        Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5, FinishReason = "stop" },
    };

    private static IDisposable PushScope(IRunRecordLogger logger) =>
        LlmCallContext.Push(new LlmCallScope(Run, Team, "sup", "sup#turn1", "supervisor.decision", logger, new NoopOffloader()));

    [Fact]
    public async Task A_structured_call_records_started_then_completed_with_kind_prompt_and_usage_off_the_scope()
    {
        var inner = new FakeClient(Completion());
        var logger = new CapturingLogger();
        var decorator = new RecordingStructuredLLMClientDecorator(inner);

        StructuredLLMCompletion result;
        using (PushScope(logger))
        {
            result = await decorator.CompleteStructuredAsync(Request(), CancellationToken.None);
        }

        result.ShouldBeSameAs(inner.StructuredResult, "the inner completion flows through verbatim — pure side-channel");

        logger.Calls.Select(c => c.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionCompleted });
        logger.Calls[0].CorrelationId.ShouldBe(logger.Calls[1].CorrelationId, "the triple is paired by one correlation id");

        var started = logger.Calls[0];
        started.RunId.ShouldBe(Run);
        started.IterationKey.ShouldBe("sup#turn1");
        started.Payload.GetProperty("kind").GetString().ShouldBe("supervisor.decision");
        started.Payload.GetProperty("provider").GetString().ShouldBe("anthropic", "the serving provider is captured so the record is self-describing under multiple providers");
        started.Payload.GetProperty("model").GetString().ShouldBe("claude-x");
        started.Payload.GetProperty("prompt").GetProperty("system").GetString().ShouldBe("SYS");
        started.Payload.GetProperty("prompt").GetProperty("user").GetString().ShouldBe("USR");

        var completed = logger.Calls[1];
        completed.Payload.GetProperty("kind").GetString().ShouldBe("supervisor.decision");
        completed.Payload.GetProperty("provider").GetString().ShouldBe("anthropic");
        completed.Payload.GetProperty("usage").GetProperty("inputTokens").GetInt32().ShouldBe(10);
        completed.Payload.GetProperty("usage").GetProperty("outputTokens").GetInt32().ShouldBe(5);
        completed.Payload.GetProperty("usage").GetProperty("finishReason").GetString().ShouldBe("stop");
        completed.Payload.GetProperty("output").GetProperty("kind").GetString().ShouldBe("plan", "the raw completion JSON is captured inline when small");
    }

    [Fact]
    public async Task Records_nothing_when_no_scope_is_pushed_and_still_returns_the_result()
    {
        var inner = new FakeClient(Completion());

        var result = await new RecordingStructuredLLMClientDecorator(inner).CompleteStructuredAsync(Request(), CancellationToken.None);

        result.ShouldBeSameAs(inner.StructuredResult, "no run scope ⇒ a pure delegate, no capture, no fault");
    }

    [Fact]
    public async Task An_inner_throw_records_started_then_failed_and_rethrows_verbatim()
    {
        var inner = new ThrowingClient(new InvalidOperationException("gateway boom"));
        var logger = new CapturingLogger();
        var decorator = new RecordingStructuredLLMClientDecorator(inner);

        using (PushScope(logger))
        {
            await Should.ThrowAsync<InvalidOperationException>(() => decorator.CompleteStructuredAsync(Request(), CancellationToken.None));
        }

        logger.Calls.Select(c => c.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionFailed });
        logger.Calls[1].Payload.GetProperty("provider").GetString().ShouldBe("anthropic");
        logger.Calls[1].Payload.GetProperty("error").GetString().ShouldBe("gateway boom");
    }

    [Fact]
    public async Task A_large_prompt_and_completion_offload_to_an_artifact_ref_not_inline()
    {
        var inner = new FakeClient(Completion());
        var logger = new CapturingLogger();
        var decorator = new RecordingStructuredLLMClientDecorator(inner);

        using (LlmCallContext.Push(new LlmCallScope(Run, Team, "sup", "sup#turn1", "supervisor.decision", logger, new OffloadingOffloader())))
        {
            await decorator.CompleteStructuredAsync(Request(), CancellationToken.None);
        }

        // The text prompt field rode as a content-addressed $artifact_id ref, not inline — with the byte size + type.
        var system = logger.Calls[0].Payload.GetProperty("prompt").GetProperty("system");
        system.GetProperty("$artifact_id").GetString().ShouldBe(OffloadingOffloader.ArtifactId.ToString());
        system.GetProperty("content_type").GetString().ShouldBe("text/plain");
        system.GetProperty("size_bytes").GetInt32().ShouldBe(Encoding.UTF8.GetByteCount("SYS"));

        // The structured completion offloaded as a JSON artifact ref.
        var output = logger.Calls[1].Payload.GetProperty("output");
        output.GetProperty("$artifact_id").GetString().ShouldBe(OffloadingOffloader.ArtifactId.ToString());
        output.GetProperty("content_type").GetString().ShouldBe("application/json");
    }

    [Fact]
    public async Task A_capture_write_failure_never_faults_the_model_call()
    {
        var inner = new FakeClient(Completion());
        var logger = new CapturingLogger { ThrowOnRecord = true };
        var completeness = new CapturingCompletenessWriter();
        var decorator = new RecordingStructuredLLMClientDecorator(inner);

        StructuredLLMCompletion result;
        using (LlmCallContext.Push(new LlmCallScope(Run, Team, "sup", "sup#turn1", "supervisor.decision", logger, new NoopOffloader(), Completeness: completeness)))
        {
            result = await decorator.CompleteStructuredAsync(Request(), CancellationToken.None);   // must NOT throw
        }

        result.ShouldBeSameAs(inner.StructuredResult, "a ledger write failure is swallowed — capture is fail-open");
        completeness.Advances.ShouldHaveSingleItem().Expected.ShouldBe(1, "capture intent is stated before the first ledger write");
        completeness.Advances.Sum(value => value.Present).ShouldBe(0, "a refused terminal row cannot be counted present");
        completeness.Gaps.Count.ShouldBe(2, "both the missing started and terminal facts remain explicit");
        completeness.Gaps.ShouldAllBe(gap => gap.Reason == CaptureGapReason.WriteRefused && gap.SubjectKind == WorkflowRunDataOwnerKinds.ModelCall);
    }

    [Fact]
    public async Task A_plain_text_call_records_started_then_completed_off_the_scope()
    {
        var inner = new PlainClient();
        var logger = new CapturingLogger();
        var decorator = new RecordingLLMClientDecorator(inner);

        LLMCompletion result;
        using (PushScope(logger))
        {
            result = await decorator.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "SYS", UserPrompt = "USR" }, CancellationToken.None);
        }

        result.Text.ShouldBe("plain-text", "the inner plain-text completion flows through verbatim — pure side-channel");

        logger.Calls.Select(c => c.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionCompleted });
        logger.Calls[0].Payload.GetProperty("prompt").GetProperty("user").GetString().ShouldBe("USR");
        logger.Calls[1].Payload.GetProperty("output").GetString().ShouldBe("plain-text", "a plain-text completion is captured as the text output");
    }

    [Fact]
    public async Task Secret_values_are_redacted_only_on_the_capture_side_while_the_provider_receives_and_returns_verbatim_content()
    {
        const string secret = "sk-live-sensitive-value";
        var inner = new EchoingPlainClient();
        var logger = new CapturingLogger();
        var decorator = new RecordingLLMClientDecorator(inner);
        var request = new LLMCompletionRequest { Model = "m", SystemPrompt = $"system {secret}", UserPrompt = $"use {secret}" };

        LLMCompletion result;
        using (LlmCallContext.Push(new LlmCallScope(Run, Team, "llm", "", "llm.complete", logger, new NoopOffloader(), CaptureRedactor: new PersistenceSecretRedactor([secret]))))
        {
            result = await decorator.CompleteAsync(request, CancellationToken.None);
        }

        inner.ObservedRequest.ShouldBeSameAs(request, "capture redaction is a persistence side-channel and must not alter model intelligence");
        result.Text.ShouldBe($"provider echoed {secret}", "the caller receives the provider's verbatim response");

        var persisted = string.Join('\n', logger.Calls.Select(call => call.Payload.GetRawText()));
        persisted.ShouldNotContain(secret, customMessage: "no interaction payload may persist a plaintext secret, including a provider echo");
        persisted.ShouldContain("[REDACTED]", customMessage: "the tape stays explicit that content was intentionally masked");
    }

    [Fact]
    public async Task Structured_secret_values_are_recursively_redacted_only_on_the_capture_side()
    {
        const string secret = "structured-sensitive-value";
        var completion = new StructuredLLMCompletion
        {
            Json = JsonSerializer.SerializeToElement(new { result = new { message = $"echo {secret}", items = new[] { "safe", secret } } }),
            Model = "claude-x",
            Usage = new LlmUsage { InputTokens = 4, OutputTokens = 2, FinishReason = "stop" },
        };
        var inner = new FakeClient(completion);
        var logger = new CapturingLogger();
        var decorator = new RecordingStructuredLLMClientDecorator(inner);
        var request = Request() with { SystemPrompt = $"system {secret}", UserPrompt = $"use {secret}" };

        StructuredLLMCompletion result;
        using (LlmCallContext.Push(new LlmCallScope(Run, Team, "llm", "", "llm.structured", logger, new NoopOffloader(), CaptureRedactor: new PersistenceSecretRedactor([secret]))))
        {
            result = await decorator.CompleteStructuredAsync(request, CancellationToken.None);
        }

        inner.ObservedStructuredRequest.ShouldBeSameAs(request, "capture redaction must not mutate the structured provider request");
        result.ShouldBeSameAs(completion, "the caller receives the provider's structured result verbatim");
        result.Json.GetProperty("result").GetProperty("message").GetString().ShouldContain(secret);

        var persisted = string.Join('\n', logger.Calls.Select(call => call.Payload.GetRawText()));
        persisted.ShouldNotContain(secret, customMessage: "recursive JSON capture must redact nested object and array values");
        persisted.ShouldContain("[REDACTED]", customMessage: "the persisted structured capture remains explicit about masking");
    }

    /// <summary>
    /// The declaration guard belongs to EVERY recording face, not just the one the integration tier drives. All three
    /// share this base's producer methods but each has its own call site, so the theory walks all three: a declaration
    /// that was refused, or that threw, may not be followed by a presence — 0171 seeds the facet at a determinate
    /// expected=0 and a present-only delta over it reads Exact — and un-states the expectation instead.
    /// </summary>
    [Theory]
    [InlineData("plain", LostDeclaration.Refused)]
    [InlineData("plain", LostDeclaration.Thrown)]
    [InlineData("structured", LostDeclaration.Refused)]
    [InlineData("structured", LostDeclaration.Thrown)]
    [InlineData("streaming", LostDeclaration.Refused)]
    [InlineData("streaming", LostDeclaration.Thrown)]
    public async Task A_lost_declaration_states_no_presence_and_unstates_the_expectation_on_every_recording_face(string face, LostDeclaration lost)
    {
        var logger = new CapturingLogger();
        var completeness = new CapturingCompletenessWriter { Lose = lost };

        using (LlmCallContext.Push(new LlmCallScope(Run, Team, "llm", "", "llm.complete", logger, new NoopOffloader(), Completeness: completeness)))
        {
            await CallEveryFaceAsync(face);
        }

        logger.Calls.Select(call => call.RecordType).ShouldContain(WorkflowRunRecordTypes.InteractionCompleted,
            customMessage: "the premise: the capture floor is untouched and only the claim about it was lost");
        completeness.Advances.ShouldBeEmpty("a declaration nobody admitted may never be followed by a present-only delta, which reads Exact over the determinate zero 0171 seeded");
        completeness.Unstated.ShouldBe(new[] { WorkflowRunDataOwnerKinds.ModelCall });
    }

    /// <summary>One call on whichever recording face the theory is walking; the streaming face is drained so its terminal capture runs.</summary>
    private static async Task CallEveryFaceAsync(string face)
    {
        var request = new LLMCompletionRequest { Model = "m", SystemPrompt = "sys", UserPrompt = "usr" };

        if (face == "plain") await new RecordingLLMClientDecorator(new PlainClient()).CompleteAsync(request, CancellationToken.None);
        if (face == "structured") await new RecordingStructuredLLMClientDecorator(new FakeClient(Completion())).CompleteStructuredAsync(Request(), CancellationToken.None);
        if (face == "streaming") await ConsumeAsync(new RecordingStreamingStructuredLLMClientDecorator(new StreamingFakeClient()), CancellationToken.None);
    }

    /// <summary>
    /// <c>Masked</c> is a claim about the BYTES this call persisted, never about whether a redactor object exists. The
    /// engine builds one for EVERY node scope from the scope's secret paths, so a constant "a redactor is configured"
    /// reports every model call masked — and <c>workflow_run_data_manifest.masked_observed</c> is a monotonic latch, so
    /// nothing later can take that claim back.
    /// </summary>
    [Theory]
    [InlineData("use sk-live-unit-secret now", true)]
    [InlineData("use nothing sensitive now", false)]
    public async Task The_masked_flag_reports_whether_content_was_actually_replaced_rather_than_whether_a_redactor_is_configured(string userPrompt, bool masked)
    {
        const string secret = "sk-live-unit-secret";
        var logger = new CapturingLogger();
        var completeness = new CapturingCompletenessWriter();
        var decorator = new RecordingLLMClientDecorator(new PlainClient());

        using (LlmCallContext.Push(new LlmCallScope(Run, Team, "llm", "", "llm.complete", logger, new NoopOffloader(), CaptureRedactor: new PersistenceSecretRedactor([secret]), Completeness: completeness)))
        {
            await decorator.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "sys", UserPrompt = userPrompt }, CancellationToken.None);
        }

        completeness.Advances.Single(advance => advance.Present > 0).Masked.ShouldBe(masked,
            customMessage: "the presence delta may only reach the redacted arm when this call's own capture actually had content replaced");
        completeness.Advances.Single(advance => advance.Expected > 0).Masked.ShouldBeFalse(
            customMessage: "a declaration is stated before any content exists to mask, so it can never be the delta that latches the redacted arm");
    }

    /// <summary>
    /// A masking observation belongs to ONE call. The scope the engine pushes lives for a whole node, and every model
    /// call the node makes reads the same redactor off it; latching there would report a later, secret-free call masked
    /// because an earlier one carried a secret.
    /// </summary>
    [Fact]
    public async Task A_masked_call_does_not_report_the_next_call_on_the_same_scope_as_masked()
    {
        const string secret = "sk-live-unit-secret";
        var logger = new CapturingLogger();
        var completeness = new CapturingCompletenessWriter();
        var decorator = new RecordingLLMClientDecorator(new PlainClient());

        using (LlmCallContext.Push(new LlmCallScope(Run, Team, "llm", "", "llm.complete", logger, new NoopOffloader(), CaptureRedactor: new PersistenceSecretRedactor([secret]), Completeness: completeness)))
        {
            await decorator.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "sys", UserPrompt = $"use {secret}" }, CancellationToken.None);
            await decorator.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "sys", UserPrompt = "nothing sensitive" }, CancellationToken.None);
        }

        completeness.Advances.Where(advance => advance.Present > 0).Select(advance => advance.Masked).ShouldBe(new[] { true, false },
            customMessage: "the masking observation is per call, so the second call's verbatim capture must not inherit the first call's replacement");
    }

    [Fact]
    public void Autofac_conditionally_wraps_each_client_so_the_recorder_mirrors_the_inners_faces()
    {
        // The THREE recording decorators are registered on mutually-exclusive conditions so the WRAPPED type mirrors the
        // inner: a structured+streaming client stays castable to BOTH IStructuredLLMClient AND IStreamingLLMClient (the
        // decider's .OfType<IStructuredLLMClient>() AND a streaming caller's .OfType<IStreamingLLMClient>() both land on
        // the recorder, not the raw client — so capture is never bypassed); a structured-only client stays structured
        // but not streaming; a plain-text-only client stays neither (the merge synthesis's `is not IStructuredLLMClient`
        // text-provider pick still finds it). One decorator implementing faces unconditionally lied about the narrower
        // clients (regression #824).
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new StreamingFakeClient()).As<ILLMClient>().As<IStructuredLLMClient>().As<IStreamingLLMClient>().SingleInstance();
        builder.RegisterInstance(new FakeClient(Completion())).As<ILLMClient>().As<IStructuredLLMClient>().SingleInstance();
        builder.RegisterInstance(new PlainClient()).As<ILLMClient>().SingleInstance();
        builder.RegisterDecorator<RecordingStreamingStructuredLLMClientDecorator, ILLMClient>(c => c.CurrentInstance is IStructuredLLMClient && c.CurrentInstance is IStreamingLLMClient);
        builder.RegisterDecorator<RecordingStructuredLLMClientDecorator, ILLMClient>(c => c.CurrentInstance is IStructuredLLMClient && c.CurrentInstance is not IStreamingLLMClient);
        builder.RegisterDecorator<RecordingLLMClientDecorator, ILLMClient>(c => c.CurrentInstance is not IStructuredLLMClient);

        using var container = builder.Build();
        var clients = container.Resolve<IEnumerable<ILLMClient>>().ToList();

        var streaming = clients.Single(c => c.Provider == "streaming");
        streaming.ShouldBeOfType<RecordingStreamingStructuredLLMClientDecorator>("a structured+streaming client is wrapped by the full-face recorder");
        streaming.ShouldBeAssignableTo<IStreamingLLMClient>("so a streaming caller's .OfType<IStreamingLLMClient>() lands on the recorder — capture is never bypassed");
        streaming.ShouldBeAssignableTo<IStructuredLLMClient>("and it still carries the structured face");

        var structured = clients.Single(c => c.Provider == "anthropic");
        structured.ShouldBeOfType<RecordingStructuredLLMClientDecorator>("a structured-only client is wrapped by the structured recorder");
        structured.ShouldBeAssignableTo<IStructuredLLMClient>();
        structured.ShouldNotBeAssignableTo<IStreamingLLMClient>("a non-streaming client must not claim the streaming face");

        var plain = clients.Single(c => c.Provider == "plain");
        plain.ShouldBeOfType<RecordingLLMClientDecorator>("a plain-text-only client is wrapped by the narrow recorder");
        plain.ShouldNotBeAssignableTo<IStructuredLLMClient>("so a `is not IStructuredLLMClient` text-provider pick still finds it — the regression that broke the merge synthesis");
    }

    [Fact]
    public async Task A_streaming_call_tees_the_events_live_and_records_started_then_completed_with_the_folded_usage()
    {
        var inner = new StreamingFakeClient();
        var logger = new CapturingLogger();
        var decorator = new RecordingStreamingStructuredLLMClientDecorator(inner);

        var events = new List<LlmStreamEvent>();
        using (PushScope(logger))
        {
            await foreach (var e in decorator.StreamAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "SYS", UserPrompt = "USR" }, CancellationToken.None))
                events.Add(e);
        }

        // The events flow through verbatim — the decorator is a live tee, not a buffer.
        events.OfType<LlmStreamEvent.TextDelta>().Select(d => d.Text).ShouldBe(new[] { "hello ", "there" });

        // AND the same authoritative pair the buffered path records brackets one bounded progressive projection.
        logger.Calls.Select(c => c.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionDelta, WorkflowRunRecordTypes.InteractionCompleted });
        logger.Calls.ShouldAllBe(c => c.CorrelationId == logger.Calls[0].CorrelationId);
        logger.Calls[0].Payload.GetProperty("prompt").GetProperty("user").GetString().ShouldBe("USR");

        var completed = logger.Calls[^1].Payload;
        completed.GetProperty("output").GetString().ShouldBe("hello there", "the streamed deltas fold into the recorded completion text");
        completed.GetProperty("usage").GetProperty("inputTokens").GetInt32().ShouldBe(5);
        completed.GetProperty("usage").GetProperty("outputTokens").GetInt32().ShouldBe(3);
        completed.GetProperty("usage").GetProperty("finishReason").GetString().ShouldBe("stop");

        logger.Calls[1].Payload.GetProperty("text").GetString().ShouldBe("hello there", "even a fast stream has one exact progressive projection; completed remains authoritative");
    }

    [Fact]
    public async Task A_200_KiB_stream_has_a_bounded_delta_write_count_exact_order_and_a_complete_authoritative_output()
    {
        // 200 KiB used to produce 800 synchronous delta-ledger transactions at the 256-char threshold. The byte-sized
        // bound makes that ceil(200/32)=7, including the final tail, while completed remains the whole-output authority.
        const int totalBytes = 200 * 1024;
        var text = new string('x', totalBytes);
        var parts = Enumerable.Repeat(new string('x', 256), totalBytes / 256).ToList();
        var logger = new CapturingLogger();
        var decorator = new RecordingStreamingStructuredLLMClientDecorator(new StreamingFakeClient { TextParts = parts }, new FakeTimeProvider());

        using (PushScope(logger))
        {
            await foreach (var _ in decorator.StreamAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u" }, CancellationToken.None)) { }
        }

        logger.Calls[0].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionStarted);
        logger.Calls[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionCompleted);

        var deltas = logger.Calls.Where(c => c.RecordType == WorkflowRunRecordTypes.InteractionDelta).ToList();
        deltas.Count.ShouldBeLessThanOrEqualTo(7, "200 KiB is at most seven 32-KiB progressive writes, not ~800 256-char transactions");
        deltas.Select(d => d.Payload.GetProperty("ordinal").GetInt32()).ShouldBe(Enumerable.Range(0, deltas.Count), "delta ordinals are monotonic from 0");
        deltas.ShouldAllBe(d => d.CorrelationId == logger.Calls[0].CorrelationId, "every delta shares the started/completed correlation id");

        var streamedText = string.Concat(deltas.Select(d => d.Payload.GetProperty("text").GetString()));
        streamedText.ShouldBe(text, "coalescing neither duplicates nor loses provider text");
        logger.Calls[^1].Payload.GetProperty("output").GetString().ShouldBe(text, "completed is still the canonical full output; deltas are only its progressive projection");
    }

    [Fact]
    public async Task A_slow_sub_threshold_stream_flushes_its_live_projection_within_one_fake_second()
    {
        var time = new FakeTimeProvider();
        var inner = new SlowStreamingClient();
        var logger = new CapturingLogger();
        var decorator = new RecordingStreamingStructuredLLMClientDecorator(inner, time);

        Task consume;
        using (PushScope(logger))
        {
            consume = ConsumeAsync(decorator, CancellationToken.None);
            await inner.WaitingAfterFirstDelta.Task.WaitAsync(TimeSpan.FromSeconds(2));

            logger.Calls.ShouldNotContain(c => c.RecordType == WorkflowRunRecordTypes.InteractionDelta, "the 4-byte fragment is below the size bound before the live timer fires");
            time.Advance(RecordingStreamingStructuredLLMClientDecorator.DeltaFlushInterval);
            await logger.DeltaRecorded.Task.WaitAsync(TimeSpan.FromSeconds(2));

            logger.Calls.Single(c => c.RecordType == WorkflowRunRecordTypes.InteractionDelta).Payload.GetProperty("text").GetString().ShouldBe("slow");
            inner.Release.TrySetResult();
            await consume;
        }
    }

    [Fact]
    public async Task A_stream_capture_failure_never_changes_or_faults_the_provider_stream()
    {
        var logger = new CapturingLogger { ThrowOnRecord = true };
        var decorator = new RecordingStreamingStructuredLLMClientDecorator(new StreamingFakeClient());
        var events = new List<LlmStreamEvent>();

        using (PushScope(logger))
        {
            await foreach (var e in decorator.StreamAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u" }, CancellationToken.None))
                events.Add(e);
        }

        events.OfType<LlmStreamEvent.TextDelta>().Select(d => d.Text).ShouldBe(new[] { "hello ", "there" }, "ledger/offload capture is a fail-open side channel, never the provider stream");
    }

    [Fact]
    public async Task A_streaming_call_records_nothing_with_no_scope_and_still_forwards_every_event()
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var e in new RecordingStreamingStructuredLLMClientDecorator(new StreamingFakeClient())
            .StreamAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u" }, CancellationToken.None))
            events.Add(e);

        events.OfType<LlmStreamEvent.TextDelta>().Select(d => d.Text).ShouldBe(new[] { "hello ", "there" }, "no run scope ⇒ a pure delegate, no capture, no fault");
    }

    [Fact]
    public async Task A_mid_stream_throw_records_started_then_failed_and_rethrows_verbatim()
    {
        var inner = new StreamingFakeClient { ThrowAfter = 1, ThrowWith = new InvalidOperationException("stream boom") };
        var logger = new CapturingLogger();
        var decorator = new RecordingStreamingStructuredLLMClientDecorator(inner);

        InvalidOperationException thrown;
        using (PushScope(logger))
        {
            thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in decorator.StreamAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u" }, CancellationToken.None)) { }
            });
        }

        thrown.ShouldBeSameAs(inner.ThrowWith, "recording must not wrap or swallow the provider's typed exception");
        logger.Calls.Select(c => c.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionFailed });
        logger.Calls[1].Payload.GetProperty("error").GetString().ShouldBe("stream boom");
        logger.Calls[1].Payload.GetProperty("failureKind").GetString().ShouldBe("exception");
    }

    [Fact]
    public async Task A_mid_stream_provider_fault_keeps_its_typed_category_and_rethrows_verbatim()
    {
        var fault = new LlmApiException("streaming", 429, LlmErrorCategory.RateLimited, "slow down");
        var inner = new StreamingFakeClient { ThrowAfter = 1, ThrowWith = fault };
        var logger = new CapturingLogger();
        var decorator = new RecordingStreamingStructuredLLMClientDecorator(inner);

        LlmApiException thrown;
        using (PushScope(logger))
        {
            thrown = await Should.ThrowAsync<LlmApiException>(async () =>
            {
                await foreach (var _ in decorator.StreamAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u" }, CancellationToken.None)) { }
            });
        }

        thrown.ShouldBeSameAs(fault);
        logger.Calls[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionFailed);
        logger.Calls[^1].Payload.GetProperty("failureKind").GetString().ShouldBe("provider");
        logger.Calls[^1].Payload.GetProperty("category").GetString().ShouldBe(nameof(LlmErrorCategory.RateLimited));
    }

    [Fact]
    public async Task A_cancelled_stream_records_a_typed_failed_terminal_and_rethrows_cancellation()
    {
        var inner = new SlowStreamingClient();
        var logger = new CapturingLogger();
        var decorator = new RecordingStreamingStructuredLLMClientDecorator(inner, new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();

        Task consume;
        using (PushScope(logger))
        {
            consume = ConsumeAsync(decorator, cancellation.Token);
            await inner.WaitingAfterFirstDelta.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => consume);
        }

        logger.Calls[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionFailed);
        logger.Calls[^1].Payload.GetProperty("failureKind").GetString().ShouldBe("cancelled");
        logger.Calls.ShouldNotContain(c => c.RecordType == WorkflowRunRecordTypes.InteractionCompleted);
    }

    [Fact]
    public async Task A_cancelled_buffered_call_records_its_failed_terminal_with_a_non_cancelled_capture_token()
    {
        var inner = new CancelledPlainClient();
        var logger = new CapturingLogger { HonorCancellation = true };
        var decorator = new RecordingLLMClientDecorator(inner);
        using var cancellation = new CancellationTokenSource();

        Task call;
        using (PushScope(logger))
        {
            call = decorator.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u" }, cancellation.Token);
            await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => call);
        }

        logger.Calls.Select(c => c.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionFailed });
        logger.Calls[^1].Payload.GetProperty("failureKind").GetString().ShouldBe("cancelled");
    }

    private static async Task ConsumeAsync(IStreamingLLMClient client, CancellationToken cancellationToken)
    {
        await foreach (var _ in client.StreamAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u" }, cancellationToken)) { }
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeClient : ILLMClient, IStructuredLLMClient
    {
        public StructuredLLMCompletion StructuredResult { get; }
        public StructuredLLMCompletionRequest? ObservedStructuredRequest { get; private set; }
        public FakeClient(StructuredLLMCompletion result) { StructuredResult = result; }
        public string Provider => "anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "t", Model = "m" });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            ObservedStructuredRequest = request;
            return Task.FromResult(StructuredResult);
        }
    }

    private sealed class PlainClient : ILLMClient
    {
        public string Provider => "plain";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "plain-text", Model = "m" });
    }

    private sealed class CancelledPlainClient : ILLMClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Provider => "plain";

        public async Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class EchoingPlainClient : ILLMClient
    {
        public LLMCompletionRequest? ObservedRequest { get; private set; }
        public string Provider => "plain";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct)
        {
            ObservedRequest = request;
            return Task.FromResult(new LLMCompletion { Text = $"provider echoed {request.UserPrompt[4..]}", Model = request.Model });
        }
    }

    private sealed class ThrowingClient : ILLMClient, IStructuredLLMClient
    {
        private readonly Exception _ex;
        public ThrowingClient(Exception ex) { _ex = ex; }
        public string Provider => "anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw _ex;
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => throw _ex;
    }

    private sealed class StreamingFakeClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
    {
        public int? ThrowAfter { get; init; }             // yield this many events, then throw ThrowWith (simulates a mid-stream gateway drop)
        public Exception? ThrowWith { get; init; }
        public IReadOnlyList<string>? TextParts { get; init; }   // override the default "hello "/"there" text fragments (for coalescing tests)

        public string Provider => "streaming";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "hello there", Model = "m" });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new StructuredLLMCompletion { Json = JsonDocument.Parse("{}").RootElement, Model = "m" });

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var parts = TextParts ?? new[] { "hello ", "there" };

            var events = new List<LlmStreamEvent> { new LlmStreamEvent.Meta(Model: "m", InputTokens: 5) };
            events.AddRange(parts.Select(p => (LlmStreamEvent)new LlmStreamEvent.TextDelta(p)));
            events.Add(new LlmStreamEvent.Meta(OutputTokens: 3, FinishReason: "stop"));

            var yielded = 0;
            foreach (var e in events)
            {
                if (ThrowAfter is { } n && yielded >= n) throw ThrowWith!;
                yield return e;
                yielded++;
                await Task.Yield();
            }
        }
    }

    private sealed class SlowStreamingClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
    {
        public TaskCompletionSource WaitingAfterFirstDelta { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Provider => "streaming";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "slow", Model = "m" });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new StructuredLLMCompletion { Json = JsonDocument.Parse("{}").RootElement, Model = "m" });

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamEvent.TextDelta("slow");
            WaitingAfterFirstDelta.TrySetResult();
            await Release.Task.WaitAsync(ct);
            yield return new LlmStreamEvent.Meta(Model: "m", OutputTokens: 1, FinishReason: "stop");
        }
    }

    private sealed class NoopOffloader : IArtifactOffloader
    {
        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken ct) => Task.FromResult(new OffloadedText(text ?? "", null));
        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken ct) => Task.FromResult(inline ?? "");
    }

    /// <summary>Forces every non-empty field to "offload" to a fixed artifact id, so the decorator's $artifact_id ref-shaping path is exercised.</summary>
    private sealed class OffloadingOffloader : IArtifactOffloader
    {
        public static readonly Guid ArtifactId = Guid.NewGuid();
        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken ct) => Task.FromResult(new OffloadedText("", ArtifactId));
        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken ct) => Task.FromResult(inline ?? "");
    }

    private sealed record Captured(Guid RunId, string RecordType, string? NodeId, string IterationKey, Guid CorrelationId, JsonElement Payload);

    private sealed class CapturingLogger : IRunRecordLogger
    {
        public List<Captured> Calls { get; } = new();
        public TaskCompletionSource DeltaRecorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowOnRecord { get; set; }
        public bool HonorCancellation { get; set; }

        public Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken cancellationToken)
        {
            if (HonorCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRecord) throw new InvalidOperationException("ledger down");
            Calls.Add(new Captured(runId, recordType, nodeId, iterationKey, correlationId, payload.Clone()));
            if (recordType == WorkflowRunRecordTypes.InteractionDelta) DeltaRecorded.TrySetResult();
            return Task.FromResult(Guid.NewGuid());
        }

        // ── Unused ──
        public Task RunQueuedAsync(Guid runId, string sourceType, Guid? actorId, CancellationToken ct) => Task.CompletedTask;
        public Task RunStartedAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task ReleaseLoadedAsync(Guid runId, int version, string definitionHash, int nodeCount, int edgeCount, CancellationToken ct) => Task.CompletedTask;
        public Task ScopeResolvedAsync(Guid runId, int wfCount, int teamCount, int sysCount, int secretPathCount, CancellationToken ct) => Task.CompletedTask;
        public Task VariablesSnapshottedAsync(Guid runId, int wfCount, int teamCount, string releaseHash, CancellationToken ct) => Task.CompletedTask;
        public Task RunCompletedAsync(Guid runId, TimeSpan duration, bool outputsPresent, CancellationToken ct) => Task.CompletedTask;
        public Task RunFailedAsync(Guid runId, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task RunCancelledAsync(Guid runId, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task RunReplayedAsync(Guid runId, Guid? parentRunId, int snapshotCount, CancellationToken ct) => Task.CompletedTask;
        public Task SupervisorRunRecoveredAsync(Guid runId, int attempt, CancellationToken ct) => Task.CompletedTask;
        public Task<Guid> NodeStartedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken ct) => Task.FromResult(Guid.Empty);
        public Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken ct) => Task.CompletedTask;
        public Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task NodeStorageUnavailableAsync(Guid runId, string nodeId, string iterationKey, string reason, string code, CancellationToken ct) => Task.CompletedTask;
        public Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken ct) => Task.CompletedTask;
        public Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken ct) => Task.CompletedTask;
        public Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken ct) => Task.FromResult((Guid.NewGuid(), Guid.NewGuid()));
        public Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken ct) => Task.CompletedTask;
        public Task WaitReissuedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, Guid waitId, Guid byUserId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingCompletenessWriter : IRunDataCompletenessWriter
    {
        /// <summary>How a declaration is lost: refused (what the real writer's containment reports) or thrown (what an empty catch used to hide). Null = every claim is admitted.</summary>
        public LostDeclaration? Lose { get; init; }

        public List<RunDataFacetAdvance> Advances { get; } = new();
        public List<WorkflowRunCaptureGap> Gaps { get; } = new();
        public List<string> Unstated { get; } = new();

        public List<RunDataManifestInitialization> Initializations { get; } = new();

        public Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken)
        {
            Initializations.Add(initialization);
            return Task.FromResult(true);
        }

        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken)
        {
            if (Lose is { } lost && advance.Expected > 0)
                return lost == LostDeclaration.Thrown ? throw new InvalidOperationException("the declaration never reached the manifest") : Task.FromResult(false);

            Advances.Add(advance);
            return Task.FromResult(true);
        }

        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken)
        {
            Gaps.Add(gap);
            return Task.FromResult(true);
        }

        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken)
        {
            Unstated.Add(facet);
            return Task.FromResult(true);
        }
    }

    /// <summary>The two ways a declaration never lands: the writer's own containment REFUSES it, or a writer THROWS it. Both are the same lost declaration.</summary>
    public enum LostDeclaration { Refused, Thrown }
}
