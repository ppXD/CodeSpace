using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Nodes;
using Shouldly;
using LogLevel = CodeSpace.Core.Services.Workflows.Lifecycle.LogLevel;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the contract of <see cref="INodeObservability"/>. These tests use a hand-rolled
/// recording double instead of a mocking library so the call sequence is readable in plain
/// assertions.
///
/// The four invariants pinned here:
///   1. <c>TraceExternalCallAsync</c> emits started → completed on success.
///   2. <c>TraceExternalCallAsync</c> emits started → failed on throw, AND re-throws.
///   3. The started + completed/failed records share one correlation id (so the run-detail
///      UI can pair them).
///   4. <c>PersistArtifactAsync</c> delegates to <see cref="IArtifactStore.PutAsync"/> with
///      the bound team id + returns the canonical ref shape.
/// </summary>
[Trait("Category", "Unit")]
public class NodeObservabilityTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid ParentRecordId = Guid.NewGuid();
    private const string NodeId = "n1";

    [Fact]
    public async Task TraceExternalCallAsync_emits_started_then_completed_on_success()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        var result = await observability.TraceExternalCallAsync(
            target: "https://api.example.com/v1/things",
            method: "GET",
            requestPayload: null,
            action: _ => Task.FromResult(42),
            cancellationToken: CancellationToken.None);

        result.ShouldBe(42, "the action's return value MUST be propagated verbatim — observability is a side channel");
        logger.StartedCalls.Count.ShouldBe(1);
        logger.CompletedCalls.Count.ShouldBe(1);
        logger.FailedCalls.ShouldBeEmpty();

        logger.StartedCalls[0].Target.ShouldBe("https://api.example.com/v1/things");
        logger.StartedCalls[0].Method.ShouldBe("GET");
        logger.StartedCalls[0].ParentRecordId.ShouldBe(ParentRecordId,
            "parent_record_id MUST chain back to the enclosing node row so the timeline tree renders");
    }

    [Fact]
    public async Task TraceExternalCallAsync_emits_started_then_failed_on_throw_and_rethrows()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await observability.TraceExternalCallAsync<int>(
                target: "https://api.example.com",
                method: "POST",
                requestPayload: null,
                action: _ => throw new InvalidOperationException("boom"),
                cancellationToken: CancellationToken.None);
        });

        ex.Message.ShouldBe("boom",
            "the action's exception MUST propagate unchanged — observability is a side channel, not a swallower");

        logger.StartedCalls.Count.ShouldBe(1);
        logger.FailedCalls.Count.ShouldBe(1);
        logger.CompletedCalls.ShouldBeEmpty();

        logger.FailedCalls[0].Error.ShouldBe("boom",
            "external_call.failed payload MUST surface the original exception message");
    }

    [Fact]
    public async Task TraceExternalCallAsync_pairs_started_and_completed_with_same_correlation_id()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        await observability.TraceExternalCallAsync(
            target: "x",
            method: "GET",
            requestPayload: null,
            action: _ => Task.FromResult("ok"),
            cancellationToken: CancellationToken.None);

        logger.CompletedCalls[0].CorrelationId.ShouldBe(logger.StartedCalls[0].CorrelationId,
            "correlation_id MUST be identical across started+completed so the UI can pair them");
    }

    [Fact]
    public async Task TraceExternalCallAsync_passes_extractor_completion_to_completed_record()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        await observability.TraceExternalCallAsync(
            target: "x",
            method: "GET",
            requestPayload: null,
            action: _ => Task.FromResult("hello"),
            completionExtractor: result => new ExternalCallCompletion { StatusCode = 200 },
            cancellationToken: CancellationToken.None);

        logger.CompletedCalls[0].StatusCode.ShouldBe(200,
            "completionExtractor MUST be invoked to decorate the completed record with protocol-level outcome");
    }

    [Fact]
    public async Task PersistArtifactAsync_delegates_to_store_with_bound_team_id()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        var bytes = System.Text.Encoding.UTF8.GetBytes("artifact-content");
        var refJson = await observability.PersistArtifactAsync("text/plain", bytes, CancellationToken.None);

        store.PutCalls.Count.ShouldBe(1);
        store.PutCalls[0].TeamId.ShouldBe(TeamId,
            "PersistArtifactAsync MUST scope to the run's owning team — cross-tenant leak otherwise");
        store.PutCalls[0].ContentType.ShouldBe("text/plain");

        // The returned ref shape pins the operator-readable contract: artifact_id + size + content_type.
        var doc = refJson;
        doc.GetProperty("artifact_id").GetGuid().ShouldNotBe(Guid.Empty);
        doc.GetProperty("size_bytes").GetInt32().ShouldBe(bytes.Length);
        doc.GetProperty("content_type").GetString().ShouldBe("text/plain");
    }

    // ─── Hand-rolled recording doubles ──────────────────────────────────────────

    private sealed class RecordingLogger : IRunRecordLogger
    {
        public List<(Guid RecordId, Guid CorrelationId, string Target, string Method, Guid? ParentRecordId)> StartedCalls { get; } = new();
        public List<(Guid CorrelationId, int? StatusCode, JsonElement? Payload)> CompletedCalls { get; } = new();
        public List<(Guid CorrelationId, string Target, string Error)> FailedCalls { get; } = new();

        public Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken cancellationToken)
        {
            var recordId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            StartedCalls.Add((recordId, correlationId, target, method, parentRecordId));
            return Task.FromResult((recordId, correlationId));
        }

        public Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken cancellationToken)
        {
            CompletedCalls.Add((correlationId, statusCode, responsePayload));
            return Task.CompletedTask;
        }

        public Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, CancellationToken cancellationToken)
        {
            FailedCalls.Add((correlationId, target, error));
            return Task.CompletedTask;
        }

        // ─── Unused members — these tests only exercise external_call.* ─────────
        public Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
        public Task RunQueuedAsync(Guid runId, string sourceType, Guid? actorId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunStartedAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseLoadedAsync(Guid runId, int version, string definitionHash, int nodeCount, int edgeCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScopeResolvedAsync(Guid runId, int wfCount, int teamCount, int sysCount, int secretPathCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VariablesSnapshottedAsync(Guid runId, int wfCount, int teamCount, string releaseHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunCompletedAsync(Guid runId, TimeSpan duration, bool outputsPresent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunFailedAsync(Guid runId, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunCancelledAsync(Guid runId, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunReplayedAsync(Guid runId, Guid? parentRunId, int snapshotCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SupervisorRunRecoveredAsync(Guid runId, int attempt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Guid> NodeStartedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
        public Task NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingArtifactStore : IArtifactStore
    {
        public List<(Guid TeamId, ReadOnlyMemory<byte> Bytes, string ContentType)> PutCalls { get; } = new();

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            PutCalls.Add((teamId, bytes, contentType));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactBytes?>(null);
        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactMetadata?>(null);
    }
}
