using System.Text.Json;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// Static, immutable descriptor of a node TYPE. Built once by the node's constructor and
/// returned from <c>INodeRuntime.Manifest</c>. Drives the editor palette, config form
/// rendering on the frontend, and the variable picker's autocomplete.
///
/// <para>Schemas are kept as raw <see cref="JsonElement"/> rather than a typed JsonSchema
/// class so plugin authors can hand-author the JSON or use any JSON-schema library.</para>
///
/// <para><b>How the schemas are actually used today</b> (do not assume more):
/// <list type="bullet">
///   <item><b>Frontend</b> — renders the config form, drives the variable-picker autocomplete,
///         and labels expected input shapes in the inspector.</item>
///   <item><b>Save-time (<c>DefinitionValidator</c>)</b> — when <see cref="OutputSchema"/> declares
///         a typed <c>properties</c> bag, downstream <c>nodes.X.outputs.Y</c> references are
///         checked against the declared keys. Reference-path SHAPE is validated; per-value
///         type conformance is NOT.</item>
///   <item><b>Runtime (engine + <c>NodeRunContext</c>)</b> — the engine does NOT validate
///         resolved input/config values against these schemas. Nodes extract values
///         defensively (<c>TryReadX</c>). A future release may add runtime schema
///         validation; today, no.</item>
/// </list></para>
/// </summary>
public sealed record NodeManifest
{
    public required string DisplayName { get; init; }

    /// <summary>UI grouping. e.g. "Triggers", "AI", "Git", "Logic". Frontend palette uses this for sectioning.</summary>
    public required string Category { get; init; }

    public required NodeKind Kind { get; init; }

    /// <summary>JSON Schema for the node's static <c>Config</c> object.</summary>
    public required JsonElement ConfigSchema { get; init; }

    /// <summary>JSON Schema for the resolved <c>Inputs</c> bag the engine passes to RunAsync.</summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>JSON Schema for the <c>Outputs</c> bag the node returns.</summary>
    public required JsonElement OutputSchema { get; init; }

    public string? Description { get; init; }

    /// <summary>Optional icon-name key the frontend can map to a lucide-react icon.</summary>
    public string? IconKey { get; init; }

    /// <summary>
    /// Named output handles. When omitted (null), the node has a single default handle ("out")
    /// and the engine follows every outgoing edge after a successful run. When set, this
    /// declares the named handles a branch / multi-output node emits (e.g. ["true", "false"]
    /// for logic.if). Edges target a specific handle via <c>EdgeDefinition.SourceHandle</c>;
    /// the engine fires only edges matching the routing hints in <c>NodeResult.RoutingHints</c>.
    /// </summary>
    public IReadOnlyList<NodeOutputHandle>? Outputs { get; init; }

    /// <summary>
    /// Opt-in marker for nodes whose <c>RunAsync</c> performs an externally-visible side
    /// effect (posting a PR comment, sending a Slack message, creating a GitHub issue). The
    /// engine USES this signal to refuse auto-resume after an abandoned-worker scenario —
    /// re-executing a side-effecting node on retry produces duplicate effects, which is worse
    /// than failing the run loudly and letting the operator decide.
    ///
    /// <para>Default <c>false</c>: pure-computation nodes (logic.if, logic.merge, flow.iterate,
    /// trigger.*) and read-only fetchers (git.fetch_pr_diff) leave it false. Side-effecting
    /// nodes (git.post_pr_comment, http.request with POST/PUT/DELETE/PATCH, llm.complete when
    /// it has billing implications) set it true.</para>
    /// </summary>
    public bool IsSideEffecting { get; init; }

    /// <summary>
    /// Marks a Trigger node that starts runs ON DEMAND (manual "Run now" / API call) instead of
    /// by subscribing to an external event source. The frontend's <c>deriveActivations</c> reads
    /// this to SKIP emitting a <c>workflow_activation</c> row for the node — a manual trigger has
    /// nothing to match incoming events against.
    ///
    /// <para>Default <c>false</c>: event triggers (trigger.pr.opened / .updated) and every
    /// non-trigger node leave it false, so existing activation derivation is unchanged. Only
    /// on-demand triggers (trigger.manual) set it true. Ignored for non-Trigger kinds.</para>
    /// </summary>
    public bool IsManual { get; init; }

    /// <summary>
    /// Opt-in marker for a node that performs a write AS a CodeSpace user's own provider identity
    /// (Model B) — e.g. <c>git.pr_review</c> submitting a review as the actor. Declaring it lets the
    /// engine GENERICALLY enforce the actor's linked identity: when such a node sits downstream of an
    /// interactive wait whose responder feeds its <see cref="ActsAsUserSpec.ActorInputKey"/>, the respond
    /// path requires the responder's identity FIRST (428 + link prompt) instead of the run failing later.
    /// New act-as-user nodes get this for free by declaring the spec — no engine or chat-layer changes.
    /// Null ⇒ the node never acts as a user.
    /// </summary>
    public ActsAsUserSpec? ActsAsUser { get; init; }

    /// <summary>
    /// Optional author-facing starter templates for this node type. Each preset is a named, ready-to-use
    /// (Config, Inputs) pair the editor offers as "start from a template" — a friendly surface over the
    /// generic schemas, so an author picks an intent (e.g. "Quorum review") instead of assembling atoms.
    /// Null/empty ⇒ the node offers no templates. Purely an editor convenience; the engine never reads it.
    /// </summary>
    public IReadOnlyList<NodePreset>? Presets { get; init; }
}

/// <summary>
/// One author-facing starter template for a node — a named, ready-to-use (Config, Inputs) pair the editor
/// applies on "start from a template". Schemas-as-data, like the manifest's other <see cref="JsonElement"/>s.
/// </summary>
public sealed record NodePreset
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required JsonElement Config { get; init; }
    public required JsonElement Inputs { get; init; }
}

/// <summary>
/// Declares HOW a node acts as a CodeSpace user's own provider identity, so the engine can enforce the
/// identity requirement generically (without hardcoding any node). Both keys name entries in the node's
/// runtime <c>Inputs</c> bag.
/// </summary>
public sealed record ActsAsUserSpec
{
    /// <summary>Input key whose value is the acting user's id. The interactive wait whose responder feeds this input is the one gated on the actor's linked identity.</summary>
    public required string ActorInputKey { get; init; }

    /// <summary>Input key whose value locates the provider the actor acts on — resolved per <see cref="ProviderSource"/>.</summary>
    public required string ProviderInputKey { get; init; }

    /// <summary>How <see cref="ProviderInputKey"/>'s value resolves to a provider instance (the unit a linked identity is scoped to).</summary>
    public required ActorProviderSource ProviderSource { get; init; }

    /// <summary>
    /// The capability this node exercises AS the actor (e.g. <c>typeof(IPullRequestReviewCapability)</c>). The
    /// respond-time gate dynamically checks the actor's token scopes against THIS capability's per-provider
    /// requirement (each provider module's <c>CapabilityScopeRequirements</c>) — so the scope a node needs
    /// follows its declared capability, nothing hardcoded. Null = skip the scope pre-check (identity + repo
    /// membership only).
    /// </summary>
    public Type? CapabilityType { get; init; }
}

/// <summary>How an act-as-user node's provider-input value resolves to a provider instance.</summary>
public enum ActorProviderSource
{
    /// <summary>The value is a repository id; the provider instance is the repo's <c>ProviderInstanceId</c>.</summary>
    Repository,

    /// <summary>The value is a provider instance id directly.</summary>
    ProviderInstance,
}

/// <summary>
/// One named output handle on a multi-output node. <see cref="Name"/> is the wire identifier
/// edges reference; <see cref="DisplayName"/> is for the editor only. Examples:
///   logic.if         → [{ Name: "true" }, { Name: "false" }]
///   logic.switch     → [{ Name: "case_a" }, { Name: "case_b" }, { Name: "default" }]
///   git.fetch_pr_diff → null (single default handle, just emits outputs to every downstream)
/// </summary>
public sealed record NodeOutputHandle
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}
