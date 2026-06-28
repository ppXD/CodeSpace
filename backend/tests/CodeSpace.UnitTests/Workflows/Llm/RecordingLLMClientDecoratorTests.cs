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
        var decorator = new RecordingLLMClientDecorator(inner);

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
        started.Payload.GetProperty("model").GetString().ShouldBe("claude-x");
        started.Payload.GetProperty("prompt").GetProperty("system").GetString().ShouldBe("SYS");
        started.Payload.GetProperty("prompt").GetProperty("user").GetString().ShouldBe("USR");

        var completed = logger.Calls[1];
        completed.Payload.GetProperty("kind").GetString().ShouldBe("supervisor.decision");
        completed.Payload.GetProperty("usage").GetProperty("inputTokens").GetInt32().ShouldBe(10);
        completed.Payload.GetProperty("usage").GetProperty("outputTokens").GetInt32().ShouldBe(5);
        completed.Payload.GetProperty("usage").GetProperty("finishReason").GetString().ShouldBe("stop");
        completed.Payload.GetProperty("output").GetProperty("kind").GetString().ShouldBe("plan", "the raw completion JSON is captured inline when small");
    }

    [Fact]
    public async Task Records_nothing_when_no_scope_is_pushed_and_still_returns_the_result()
    {
        var inner = new FakeClient(Completion());

        var result = await new RecordingLLMClientDecorator(inner).CompleteStructuredAsync(Request(), CancellationToken.None);

        result.ShouldBeSameAs(inner.StructuredResult, "no run scope ⇒ a pure delegate, no capture, no fault");
    }

    [Fact]
    public async Task An_inner_throw_records_started_then_failed_and_rethrows_verbatim()
    {
        var inner = new ThrowingClient(new InvalidOperationException("gateway boom"));
        var logger = new CapturingLogger();
        var decorator = new RecordingLLMClientDecorator(inner);

        using (PushScope(logger))
        {
            await Should.ThrowAsync<InvalidOperationException>(() => decorator.CompleteStructuredAsync(Request(), CancellationToken.None));
        }

        logger.Calls.Select(c => c.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionFailed });
        logger.Calls[1].Payload.GetProperty("error").GetString().ShouldBe("gateway boom");
    }

    [Fact]
    public async Task A_capture_write_failure_never_faults_the_model_call()
    {
        var inner = new FakeClient(Completion());
        var logger = new CapturingLogger { ThrowOnRecord = true };
        var decorator = new RecordingLLMClientDecorator(inner);

        StructuredLLMCompletion result;
        using (PushScope(logger))
        {
            result = await decorator.CompleteStructuredAsync(Request(), CancellationToken.None);   // must NOT throw
        }

        result.ShouldBeSameAs(inner.StructuredResult, "a ledger write failure is swallowed — capture is fail-open");
    }

    [Fact]
    public void Autofac_decorates_ILLMClient_so_the_registry_cast_to_IStructuredLLMClient_lands_on_the_recorder()
    {
        // Mirrors the real wiring: a provider registers as BOTH interfaces; the decorator is registered over ILLMClient
        // (the interface LLMClientRegistry holds + the decider casts). Resolving the registry's IEnumerable<ILLMClient>
        // must yield the decorator, and the .OfType<IStructuredLLMClient>() cast the decider does must land on it.
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new FakeClient(Completion())).As<ILLMClient>().As<IStructuredLLMClient>().SingleInstance();
        builder.RegisterDecorator<RecordingLLMClientDecorator, ILLMClient>();

        using var container = builder.Build();

        var asILLMClient = container.Resolve<IEnumerable<ILLMClient>>().Single();
        asILLMClient.ShouldBeOfType<RecordingLLMClientDecorator>("the registry's ILLMClient enumeration is decorated");
        asILLMClient.ShouldBeAssignableTo<IStructuredLLMClient>("so the decider's .OfType<IStructuredLLMClient>() cast lands on the recorder, not the raw client");
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
