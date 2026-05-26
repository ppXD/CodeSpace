using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// One instance per node invocation — the engine binds the invocation's (runId, nodeId,
/// teamId, parentRecordId) into the constructor so the node surface stays "describe the call"
/// without identity plumbing.
///
/// <para>Not registered in DI: the engine news this up per-node in <c>ExecuteNodeAsync</c>.
/// Doing it inline means we don't have to thread <c>NodeId</c> / <c>RunId</c> through
/// AsyncLocal or a scoped factory — the binding is explicit at construction time.</para>
/// </summary>
public sealed class NodeObservability : INodeObservability
{
    /// <summary>
    /// No-op handle for unit tests that exercise node logic without an engine. Production
    /// code MUST receive an engine-built <see cref="NodeObservability"/> — the no-op
    /// silently drops ledger emissions, which would mask bugs in real runs.
    /// </summary>
    public static INodeObservability NoOp { get; } = new NoOpObservability();

    private readonly IRunRecordLogger _recordLogger;
    private readonly IArtifactStore _artifactStore;
    private readonly Guid _runId;
    private readonly string _nodeId;
    private readonly Guid _teamId;
    private readonly Guid _parentRecordId;

    public NodeObservability(IRunRecordLogger recordLogger, IArtifactStore artifactStore, Guid runId, string nodeId, Guid teamId, Guid parentRecordId)
    {
        _recordLogger = recordLogger;
        _artifactStore = artifactStore;
        _runId = runId;
        _nodeId = nodeId;
        _teamId = teamId;
        _parentRecordId = parentRecordId;
    }

    public async Task<TResult> TraceExternalCallAsync<TResult>(string target, string method, JsonElement? requestPayload, Func<CancellationToken, Task<TResult>> action, Func<TResult, ExternalCallCompletion>? completionExtractor = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var (_, correlationId) = await _recordLogger.ExternalCallStartedAsync(_runId, _nodeId, target, method, requestPayload, _parentRecordId, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            var duration = DateTimeOffset.UtcNow - startedAt;
            var completion = completionExtractor?.Invoke(result);
            await _recordLogger.ExternalCallCompletedAsync(_runId, _nodeId, correlationId, completion?.StatusCode, completion?.ResponsePayload, duration, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Operator cancel — don't re-emit as failure; the run-level cancel record handles it.
            // We still want to leave a trace so the timeline shows "this call was in flight when
            // we cancelled" — emit failed with the cancellation message, then re-throw.
            var duration = DateTimeOffset.UtcNow - startedAt;
            await _recordLogger.ExternalCallFailedAsync(_runId, _nodeId, correlationId, target, "Operation cancelled.", duration, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var duration = DateTimeOffset.UtcNow - startedAt;
            await _recordLogger.ExternalCallFailedAsync(_runId, _nodeId, correlationId, target, ex.Message, duration, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> PersistArtifactAsync(string contentType, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var artifactId = await _artifactStore.PutAsync(_teamId, bytes, contentType, cancellationToken).ConfigureAwait(false);

        // Build the canonical artifact-ref shape. Operators reading the ledger see:
        //   {"$artifact_id":"...","size_bytes":N,"content_type":"..."}
        // The "$" prefix matches the existing $ref convention for structured references —
        // makes it visually obvious in payload JSON that the value points elsewhere.
        var refJson = JsonSerializer.SerializeToElement(new
        {
            artifact_id = artifactId,
            size_bytes = bytes.Length,
            content_type = contentType,
        });

        return refJson;
    }

    /// <summary>
    /// Test-only no-op. Calls the action straight through, no records, no artifacts. Never
    /// used in production — engine always supplies a real <see cref="NodeObservability"/>.
    /// </summary>
    private sealed class NoOpObservability : INodeObservability
    {
        public Task<TResult> TraceExternalCallAsync<TResult>(string target, string method, JsonElement? requestPayload, Func<CancellationToken, Task<TResult>> action, Func<TResult, ExternalCallCompletion>? completionExtractor = null, CancellationToken cancellationToken = default) =>
            action(cancellationToken);

        public Task<JsonElement> PersistArtifactAsync(string contentType, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { artifact_id = Guid.Empty, size_bytes = bytes.Length, content_type = contentType }));
    }
}
