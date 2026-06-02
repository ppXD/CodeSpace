using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// The per-run variable scope. The engine builds + maintains this as the run proceeds —
/// the trigger node's payload lives under <c>trigger.*</c>, every subsequent node's outputs
/// land under <c>nodes.&lt;id&gt;.outputs.*</c>. The <c>VariableResolver</c> walks dotted
/// paths against this object.
///
/// <para>Eight read-side buckets are exposed at runtime — one per template head the
/// resolver knows about:</para>
/// <list type="bullet">
///   <item><c>{{trigger.X}}</c> → <see cref="Trigger"/></item>
///   <item><c>{{nodes.&lt;id&gt;.outputs.X}}</c> → <see cref="Nodes"/></item>
///   <item><c>{{team.X}}</c> → <see cref="Team"/></item>
///   <item><c>{{wf.X}}</c> → <see cref="Wf"/></item>
///   <item><c>{{input.X}}</c> → <see cref="Input"/></item>
///   <item><c>{{sys.X}}</c> → <see cref="Sys"/></item>
///   <item><c>{{project.&lt;slug&gt;.X}}</c> → <see cref="Projects"/></item>
///   <item><c>{{item}}</c> / <c>{{index}}</c> → <see cref="Iteration"/> (only inside
///         flow.iterate; null otherwise)</item>
/// </list>
/// <para>Plus <see cref="SecretPaths"/>, which is metadata for the Terminal-output
/// secret-leak guard — not a resolvable template head. Adding a new scope category
/// requires deliberate engine work (resolver dispatch + scope-build + the editor's
/// system-variables tab); plugin authors cannot extend the shape from outside. This
/// protects the path syntax from drift.</para>
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
    /// Loop-scoped variables for nodes inside a <c>flow.loop</c> body — referenced via
    /// <c>{{loop.&lt;name&gt;}}</c>. Carries the loop's mutable variables plus <c>{{loop.index}}</c>
    /// (0-based pass number). Null outside a loop body. Unlike <see cref="Iteration"/> (which the
    /// resolver reads via BARE <c>{{item}}</c>), this is an explicit <c>loop.</c>-prefixed head so a
    /// body can reference both its own loop vars and an enclosing iterate's <c>{{item}}</c> without
    /// collision. Each enclosing loop swaps this slot for its body's pass (save/restore for nesting).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Loop { get; init; }

    /// <summary>
    /// Workflow-scoped variables — referenced via <c>{{wf.&lt;name&gt;}}</c>. Sourced from the
    /// unified <c>variable</c> table with <c>scope='Workflow'</c> (NOT from a
    /// <c>WorkflowDefinition.Variables[]</c> array — that field was removed in Phase 2.7;
    /// values now live in the variable table and are CRUD'd through
    /// <c>WorkflowVariablesController</c>). Engine pours all active rows for the run's
    /// workflow in via <see cref="Variables.IVariableService.GetAllForEngineAsync"/> at
    /// scope-build time; secret-typed rows are decrypted in that same call. Snapshotted on
    /// first run for replay determinism.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Wf { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Per-run input parameters. Referenced via <c>{{input.&lt;name&gt;}}</c>. Populated from
    /// the run's normalised trigger payload (manual run form / HTTP body / event-trigger
    /// normalised fields); defaults from <c>WorkflowDefinition.Inputs[].Default</c> apply when
    /// the caller omits a non-required input.
    ///
    /// <para><b>Not</b> JSON-Schema-validated at run-start. <c>WorkflowDefinition.Inputs[].Schema</c>
    /// is consumed by the frontend (form rendering + picker autocomplete) and the
    /// save-time <c>DefinitionValidator</c> reference-path check, but the engine does not
    /// enforce per-value schema conformance at runtime — nodes extract values defensively
    /// (the <c>TryReadX</c> pattern). Missing REQUIRED inputs ARE enforced (fail-fast at
    /// scope build — see <c>MissingRequiredInputValidator</c>); per-value schema conformance
    /// is not.</para>
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
    /// Phase 3.0 — Project-scoped variables, keyed by project slug then by variable name.
    /// Referenced via <c>{{project.&lt;slug&gt;.&lt;name&gt;}}</c>. Same secret/non-secret
    /// rules as Team/Wf scope; secret rows are pre-decrypted in the bag, and their
    /// fully-qualified paths land in <see cref="SecretPaths"/> for the Terminal-output guard.
    ///
    /// <para>Build strategy: the engine scans the workflow definition's references at
    /// scope-build time and only loads variables for projects actually referenced. This
    /// keeps the runtime cost proportional to what the workflow uses, not to the team's
    /// total project count. References to non-existent slugs OR slugs in other teams
    /// fail save-time validation, so the engine can trust the slug→project.id mapping it
    /// resolves here.</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Projects { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>();

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
