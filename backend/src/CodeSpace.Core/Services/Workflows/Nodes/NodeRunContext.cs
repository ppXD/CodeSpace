using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// Per-invocation context handed to <c>INodeRuntime.RunAsync</c>. Inputs and Config arrive
/// already resolved — the engine has run <c>VariableResolver</c> over the raw NodeDefinition
/// values, so the node sees concrete values (strings, numbers, objects), never raw
/// <c>{{ref}}</c> templates.
///
/// <para><b>What is NOT guaranteed:</b> the engine does NOT JSON-Schema-validate the
/// resolved <see cref="Inputs"/> / <see cref="Config"/> against the manifest's
/// <c>InputSchema</c> / <c>ConfigSchema</c> at runtime. Schemas are used at save time by
/// <c>DefinitionValidator</c> (reference-path shape + upstream output-key membership) and
/// by the frontend (form rendering, picker autocomplete). At RunAsync time a node receives
/// whatever the resolver produced — including <c>null</c> when a referenced scope value
/// was missing — and MUST extract values defensively (the <c>TryReadX</c> pattern used by
/// every built-in node). Strict per-value schema validation may be added in a future
/// release; today, treat the
/// manifest schemas as documentation + UI-driver, not a runtime contract.</para>
/// </summary>
public sealed record NodeRunContext
{
    /// <summary>
    /// Resolved inputs — every <c>{{ref}}</c> template was substituted by
    /// <c>VariableResolver</c>. Values that referenced a missing scope key resolve to
    /// <c>JsonValueKind.Null</c>; extract defensively. Not JSON-Schema-validated at runtime
    /// (see the record-level remark).
    /// </summary>
    public required IReadOnlyDictionary<string, JsonElement> Inputs { get; init; }

    /// <summary>
    /// Resolved static config — same resolution + null semantics as <see cref="Inputs"/>.
    /// Not JSON-Schema-validated at runtime (see the record-level remark).
    /// </summary>
    public required IReadOnlyDictionary<string, JsonElement> Config { get; init; }

    /// <summary>
    /// <see cref="Config"/> with every secret-referencing key replaced by a redaction marker (the engine's per-node
    /// <c>IPayloadRedactor.RedactBag</c> output — the same redaction the node.started record gets). A node that persists
    /// config text to a HUMAN surface that outlives the run (a suspend payload the queue / run-detail reads, an
    /// approval prompt) MUST read those human-facing fields from here, NOT <see cref="Config"/>, so a <c>{{team.SECRET}}</c>
    /// in author-written decision/approval text never lands as plaintext. NULL only when no caller set it (tests that
    /// don't exercise redaction); the engine ALWAYS sets it, so a node falls back to <see cref="Config"/> only off the
    /// engine path. Functional (non-human) config — read straight from <see cref="Config"/> as before.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? RedactedConfig { get; init; }

    /// <summary>
    /// Raw, un-resolved config from the NodeDefinition — {{ref}} templates still intact.
    /// Iteration-style nodes (flow.iterate) use this to re-resolve templates per iteration
    /// with a different (item/index-populated) scope. Most nodes should use <see cref="Config"/>.
    /// </summary>
    public required JsonElement RawConfig { get; init; }

    /// <summary>Raw, un-resolved inputs. Mirror of <see cref="RawConfig"/> for iteration nodes that need to re-resolve their inputs per iteration.</summary>
    public required JsonElement RawInputs { get; init; }

    /// <summary>
    /// Read-only access to the engine's run scope (trigger payload + previously-executed
    /// nodes' outputs). Most nodes won't need this — they should rely on declared
    /// <see cref="Inputs"/> instead. Exposed for the rare case where a node legitimately
    /// needs to introspect (e.g. logic.merge picking the first non-null upstream).
    /// </summary>
    public required NodeRunScope Scope { get; init; }

    /// <summary>Per-node logger. Already scoped to (workflow_run.id, node.id) — node just logs.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// This node's own id within the definition. Set by the engine. Almost no node needs it — the engine
    /// owns identity/persistence — but a SELF-RE-ENTRANT node (e.g. <c>agent.supervisor</c>) uses it to mint a
    /// per-suspension <see cref="SuspensionToken.IterationKey"/> (<c>&lt;nodeId&gt;#turn{N}</c>) so the same
    /// node parks a distinct wait row each turn. Empty default keeps every other node + test untouched.
    /// </summary>
    public string NodeId { get; init; } = "";

    /// <summary>
    /// Per-node observability handle. Nodes use this to wrap external calls (HTTP, LLM, Git
    /// provider APIs) and to persist large payloads as artifacts. See
    /// <see cref="INodeObservability"/> for the contract.
    /// </summary>
    public required INodeObservability Observability { get; init; }

    /// <summary>
    /// Set ONLY when the node is being re-run after a suspension was resolved — carries the
    /// resume signal's payload (timer wake marker, the approver's decision, the callback body).
    /// <c>null</c> on a node's first execution. A suspending node (e.g. flow.sleep, a future
    /// flow.wait_approval) inspects this to decide "first pass → return <c>Suspend(token)</c>"
    /// vs "resumed → return <c>Success</c>".
    /// </summary>
    public JsonElement? ResumePayload { get; init; }

    /// <summary>
    /// Set ONLY when this is a RESPAWN attempt (the engine's retry loop is re-running the node fresh after a
    /// retryable failure delivered by a settled <see cref="ResumePayload"/>) — carries THAT retiring payload, one
    /// cycle stale, so a node whose prior attempt captured a resumable session can warm-continue it instead of
    /// cold-starting (e.g. <c>agent.run</c> reading its own <c>sessionId</c>/<c>sessionTranscript</c> keys to stamp
    /// <c>AgentTask.ResumeFromSessionId</c>). <c>null</c> on a node's first attempt, and on every in-process
    /// (non-suspending) retry. A node that doesn't recognize its own payload shape simply finds nothing to read —
    /// byte-identical no-op for every other node type.
    /// </summary>
    public JsonElement? PriorAttemptPayload { get; init; }
}
