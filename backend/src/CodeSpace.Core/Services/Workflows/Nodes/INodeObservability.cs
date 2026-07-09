using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// Per-node observability handle. Nodes that make external calls (HTTP, LLM, Git provider
/// APIs) MUST wrap them with <see cref="TraceExternalCallAsync"/> so the ledger captures
/// a start/complete pair (or start/failed on throw). The handle also fronts
/// <see cref="IArtifactStore"/> for persisting large payloads so the ledger never grows
/// unbounded — the record stores a small reference, the bytes live in <c>workflow_artifact</c>.
///
/// <para>Lifecycle: the engine builds one per node invocation when constructing
/// <see cref="NodeRunContext"/>. Nodes receive it as <c>context.Observability</c>; the
/// per-invocation identity (run / node / parent_record) is baked in by the engine, so the
/// node just describes WHAT it's calling — not WHICH run is calling.</para>
///
/// <para>Why a helper instead of exposing <c>IRunRecordLogger</c> directly: the start/complete
/// pattern is easy to get wrong (forget to fire the completed record on the failure branch,
/// pass the wrong correlation id, forget to capture the duration). One method that runs the
/// action AND emits both ends eliminates an entire category of forgotten-ledger-entry bugs.</para>
/// </summary>
public interface INodeObservability
{
    /// <summary>
    /// Wrap an external call with ledger emission. Behaviour:
    /// <list type="number">
    ///   <item>Emits <c>external_call.started</c> with <paramref name="target"/> /
    ///         <paramref name="method"/> / <paramref name="requestPayload"/> + a fresh
    ///         correlation id, linked back to the enclosing node row via parent_record_id.</item>
    ///   <item>Invokes <paramref name="action"/> and awaits its result.</item>
    ///   <item>On success: emits <c>external_call.completed</c> with the same correlation id.
    ///         If <paramref name="completionExtractor"/> is supplied, its return value
    ///         decorates the completed record (status code, response payload reference).</item>
    ///   <item>On throw: emits <c>external_call.failed</c> with the exception's message,
    ///         then re-throws so the node's own failure semantics are unchanged.</item>
    /// </list>
    ///
    /// <para>The trace is a SIDE CHANNEL: the action's return value is propagated verbatim to
    /// the caller; observability never alters the call's outcome.</para>
    /// </summary>
    Task<TResult> TraceExternalCallAsync<TResult>(string target, string method, JsonElement? requestPayload, Func<CancellationToken, Task<TResult>> action, Func<TResult, ExternalCallCompletion>? completionExtractor = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The STREAMING counterpart of <see cref="TraceExternalCallAsync"/> — for a call whose result arrives as an
    /// <see cref="IAsyncEnumerable{T}"/> the buffered <c>Func&lt;CT,Task&lt;TResult&gt;&gt;</c> shape can't carry. Emits
    /// <c>external_call.started</c> before the first event, yields every event VERBATIM (a live tee — the caller folds
    /// them), then emits <c>external_call.completed</c> after the stream ends, or <c>external_call.failed</c> on a
    /// mid-stream throw / cancellation. The rich model facts (usage / output) ride the <c>interaction.*</c> records the
    /// client seam writes; this trace is the call-plumbing bracket, so its completed record is bare.
    /// </summary>
    IAsyncEnumerable<TEvent> TraceExternalStreamAsync<TEvent>(string target, string method, JsonElement? requestPayload, Func<CancellationToken, IAsyncEnumerable<TEvent>> stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist <paramref name="bytes"/> as a workflow artifact, scoped to the team that owns
    /// the run. Returns a JSON reference of the form
    /// <c>{"$artifact_id":"&lt;guid&gt;","size_bytes":N,"content_type":"..."}</c> suitable for
    /// inclusion in ledger payload JSON. Large HTTP response bodies, LLM completions, and
    /// fetched diffs should go through here instead of being inlined in PayloadJson — the
    /// ledger stays small + searchable, the heavy content sits in <c>workflow_artifact</c>
    /// where it's content-addressable and dedupable.
    /// </summary>
    Task<JsonElement> PersistArtifactAsync(string contentType, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional decoration for <see cref="INodeObservability.TraceExternalCallAsync"/>'s completed
/// record. Nodes use this to surface protocol-level outcome (HTTP status, an artifact ref to
/// the response body, etc) on top of the bare "the call returned" signal.
/// </summary>
public sealed record ExternalCallCompletion
{
    /// <summary>Protocol-level status (HTTP status code, gRPC status, etc). Null if not applicable.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Optional structured response summary persisted in the completed record's payload_json.</summary>
    public JsonElement? ResponsePayload { get; init; }
}
