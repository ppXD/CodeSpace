using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Constants;
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
        var decorator = new RecordingStructuredLLMClientDecorator(inner);

        StructuredLLMCompletion result;
        using (PushScope(logger))
        {
            result = await decorator.CompleteStructuredAsync(Request(), CancellationToken.None);   // must NOT throw
        }

        result.ShouldBeSameAs(inner.StructuredResult, "a ledger write failure is swallowed — capture is fail-open");
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
    public void Autofac_conditionally_wraps_a_structured_client_as_structured_and_a_plain_client_as_plain()
    {
        // The TWO recording decorators are registered conditionally so the WRAPPED type mirrors the inner: a
        // structured-capable client stays IStructuredLLMClient (the decider's .OfType<IStructuredLLMClient>() cast lands
        // on it), a plain-text-only client stays non-structured (the merge synthesis's `is not IStructuredLLMClient`
        // text-provider pick still finds it). The regression #824 introduced: a single decorator implementing BOTH
        // unconditionally lied about the plain client → the synthesis fell through to the wrong client.
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new FakeClient(Completion())).As<ILLMClient>().As<IStructuredLLMClient>().SingleInstance();
        builder.RegisterInstance(new PlainClient()).As<ILLMClient>().SingleInstance();
        builder.RegisterDecorator<RecordingStructuredLLMClientDecorator, ILLMClient>(c => c.CurrentInstance is IStructuredLLMClient);
        builder.RegisterDecorator<RecordingLLMClientDecorator, ILLMClient>(c => c.CurrentInstance is not IStructuredLLMClient);

        using var container = builder.Build();
        var clients = container.Resolve<IEnumerable<ILLMClient>>().ToList();

        var structured = clients.Single(c => c.Provider == "anthropic");
        structured.ShouldBeOfType<RecordingStructuredLLMClientDecorator>("a structured client is wrapped by the structured recorder");
        structured.ShouldBeAssignableTo<IStructuredLLMClient>("so the decider's .OfType<IStructuredLLMClient>() cast lands on the recorder, not the raw client");

        var plain = clients.Single(c => c.Provider == "plain");
        plain.ShouldBeOfType<RecordingLLMClientDecorator>("a plain-text-only client is wrapped by the narrow recorder");
        plain.ShouldNotBeAssignableTo<IStructuredLLMClient>("so a `is not IStructuredLLMClient` text-provider pick still finds it — the regression that broke the merge synthesis");
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeClient : ILLMClient, IStructuredLLMClient
    {
        public StructuredLLMCompletion StructuredResult { get; }
        public FakeClient(StructuredLLMCompletion result) { StructuredResult = result; }
        public string Provider => "anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "t", Model = "m" });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => Task.FromResult(StructuredResult);
    }

    private sealed class PlainClient : ILLMClient
    {
        public string Provider => "plain";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "plain-text", Model = "m" });
    }

    private sealed class ThrowingClient : ILLMClient, IStructuredLLMClient
    {
        private readonly Exception _ex;
        public ThrowingClient(Exception ex) { _ex = ex; }
        public string Provider => "anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw _ex;
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => throw _ex;
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
        public bool ThrowOnRecord { get; set; }

        public Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken cancellationToken)
        {
            if (ThrowOnRecord) throw new InvalidOperationException("ledger down");
            Calls.Add(new Captured(runId, recordType, nodeId, iterationKey, correlationId, payload.Clone()));
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
        public Task NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken ct) => Task.CompletedTask;
        public Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken ct) => Task.CompletedTask;
        public Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken ct) => Task.CompletedTask;
        public Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken ct) => Task.FromResult((Guid.NewGuid(), Guid.NewGuid()));
        public Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken ct) => Task.CompletedTask;
    }
}
