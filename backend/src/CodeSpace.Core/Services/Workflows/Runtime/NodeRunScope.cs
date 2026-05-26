using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// The per-run variable scope. The engine builds + maintains this as the run proceeds —
/// the trigger node's outputs land under <c>trigger.*</c>, every subsequent node's outputs
/// land under <c>nodes.&lt;id&gt;.outputs.*</c>. The <c>VariableResolver</c> walks dotted
/// paths against this object.
///
/// The shape is INTENTIONALLY minimal — only three top-level keys (trigger, nodes, env).
/// Adding a new scope category requires deliberate engine work (and likely a new built-in
/// node like <c>env.set</c>); plugin authors cannot extend the scope shape from outside.
/// This protects the path syntax from drift.
/// </summary>
public sealed class NodeRunScope
{
    /// <summary>Trigger payload + the trigger node's emitted outputs, merged under one bag.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Trigger { get; init; }

    /// <summary>
    /// Each completed node's persisted outputs keyed by NodeDefinition.Id. Engine writes
    /// to this AFTER each successful node — downstream nodes can read upstream outputs
    /// as <c>nodes.&lt;id&gt;.outputs.&lt;key&gt;</c>.
    /// </summary>
    public IDictionary<string, IReadOnlyDictionary<string, JsonElement>> Nodes { get; }
        = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();

    /// <summary>
    /// Team-scoped variables — referenced via <c>{{team.&lt;name&gt;}}</c>. Sourced from the
    /// unified <c>variable</c> table with <c>scope='Team'</c>. Engine pours all active rows
    /// for the run's team in via <see cref="Variables.IVariableService.GetAllForEngineAsync"/>
    /// at scope-build time; secret-typed rows are decrypted in that same call.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Team { get; init; }
        = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Per-iteration variables for nodes inside a flow.iterate (or future flow.subworkflow).
    /// Null for non-iteration scopes. When populated, references like {{item}}, {{index}}
    /// resolve through here BEFORE falling through to the regular three buckets.
    ///
    /// The contract is intentionally loose — each iteration node decides what keys to set.
    /// flow.iterate puts {item, index}; future loop variants might add {iteration_id, total}.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Iteration { get; init; }

    /// <summary>
    /// Workflow-author-defined constants. Referenced via <c>{{wf.&lt;name&gt;}}</c>. Built once
    /// per run from <c>WorkflowDefinition.Variables[].Default</c>. Immutable for the duration
    /// of a run — variables are design-time constants, not per-run mutables (inputs are the
    /// per-run channel).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Wf { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Per-run input parameters. Referenced via <c>{{input.&lt;name&gt;}}</c>. Populated from
    /// the run's trigger payload (manual run form / HTTP body / event-trigger normalised
    /// fields) and validated against <c>WorkflowDefinition.Inputs[].Schema</c> at run-start.
    /// Defaults from the definition apply when the caller omits a non-required input.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Input { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Auto-populated system context — IDs + timestamps the engine emits on every run so
    /// any node can carry them into downstream calls (log lines, webhook signatures, audit
    /// trails). Referenced via <c>{{sys.&lt;key&gt;}}</c>. The set of keys is fixed by the
    /// engine (see <see cref="SystemScopeKeys"/>) — plugin authors don't extend it. This
    /// is the equivalent of "system variables" in other workflow platforms.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Sys { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Fully-qualified dotted paths for every scope entry that came from a <c>Secret</c>-typed
    /// variable (e.g. <c>"team.API_KEY"</c>, <c>"wf.DB_PASSWORD"</c>). Populated at scope-build
    /// time from <see cref="Variables.IVariableService.GetAllForEngineAsync"/> by inspecting
    /// each entry's <c>ValueType</c>.
    ///
    /// <para>The engine uses this set to block any Terminal node from writing a secret-sourced
    /// value into <c>workflow_run.OutputsJson</c>: secrets are designed for internal node
    /// consumption (HTTP auth headers, LLM API keys) — surfacing them as workflow outputs would
    /// (a) persist plaintext in the runs table, accessible to anyone with run-view permission,
    /// and (b) leak them to external callers reading the workflow's output contract. The check
    /// scans the Terminal's <c>inputs</c> template tree for {{path}} / $ref references that
    /// resolve to a secret path and fails the run with a clear error.</para>
    ///
    /// <para>Secrets in node Config or Inputs (consumed in-process by HttpRequestNode, etc.) are
    /// fine — they're sent over the wire to whatever provider but never persisted to OutputsJson.</para>
    /// </summary>
    public IReadOnlySet<string> SecretPaths { get; init; } = new HashSet<string>();
}

/// <summary>
/// Canonical list of every <c>sys.*</c> key the engine populates. Kept in one place so the
/// editor's system-variables tab, the autocomplete picker, the validator, and the engine
/// can never drift. Add a new key in two places (key constant + <see cref="Descriptors"/>
/// entry), populate it in <c>WorkflowEngine.BuildSysScope</c>, and the editor picks it up
/// via <c>GET /api/workflows/system-variables</c>.
/// </summary>
public static class SystemScopeKeys
{
    public const string WorkflowId = "workflow_id";
    public const string WorkflowRunId = "workflow_run_id";
    public const string WorkflowVersion = "workflow_version";

    /// <summary>Open-string source identifier from the upstream run request (e.g. "manual", "provider.github.pull_request").</summary>
    public const string SourceType = "source_type";

    public const string StartedAt = "started_at";
    public const string TeamId = "team_id";
    public const string UserId = "user_id";

    /// <summary>Full set, in the order the editor's tab should display them.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        WorkflowId, WorkflowRunId, WorkflowVersion, SourceType, StartedAt, TeamId, UserId
    };

    /// <summary>
    /// One descriptor per key, with type + one-line operator-facing description. Display
    /// order matches <see cref="All"/> so the editor's read-only system tab and the
    /// autocomplete picker render the keys in the same sequence.
    /// </summary>
    public static readonly IReadOnlyList<SystemScopeDescriptor> Descriptors = new SystemScopeDescriptor[]
    {
        new(WorkflowId,      "string",  "The workflow definition's UUID."),
        new(WorkflowRunId,   "string",  "This run's UUID — unique per invocation."),
        new(WorkflowVersion, "integer", "The snapshotted definition version this run executes against."),
        new(SourceType,      "string",  "Open-string source identifier from the upstream run request — e.g. \"manual\", \"replay\", \"provider.github.pull_request\"."),
        new(StartedAt,       "string",  "ISO-8601 timestamp of when the engine began this run."),
        new(TeamId,          "string",  "The team the workflow belongs to."),
        new(UserId,          "string",  "The user who started the run; null for webhook / cron runs."),
    };
}

/// <summary>
/// Metadata about a single <c>sys.*</c> key — the wire format for
/// <c>GET /api/workflows/system-variables</c>. Mirrored verbatim by the frontend so the
/// editor's read-only system tab + the {{}} autocomplete picker know type + description
/// for every key without hardcoding a parallel list.
/// </summary>
public sealed record SystemScopeDescriptor(string Key, string Type, string Description);
