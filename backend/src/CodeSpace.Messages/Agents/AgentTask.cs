using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The substrate-neutral "agent task envelope" — what the workflow hands to the agent layer. It says
/// WHAT to do (goal), with WHICH harness + model, and under what permissions; it never mentions a CLI
/// flag. Each <see cref="IAgentHarness"/> translates this into its own invocation, so swapping harness
/// or runner never touches the Workflow domain.
///
/// B0.2 carries the fields the harness consumes to build an invocation. Repo/branch resolution,
/// expected-outputs, and credential refs are added when AgentRunService (B0.3) owns workspace prep.
/// </summary>
public sealed record AgentTask
{
    /// <summary>Natural-language goal / prompt for the agent.</summary>
    public required string Goal { get; init; }

    /// <summary>Harness kind to run this task — resolved via <see cref="IAgentHarnessRegistry"/> (e.g. "codex-cli").</summary>
    public required string Harness { get; init; }

    /// <summary>Model id within the chosen harness's <see cref="IAgentHarness.Models"/> catalog, or null/blank to let the harness pick its own default (the Model=empty rule).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Optional Agent persona (<c>AgentDefinition</c>) this run resolves from — null = a pure-inline run (no persona).
    /// When set, the dispatch-time <c>IAgentDefinitionResolver</c> merges the persona's system prompt + model into this
    /// task before the run is persisted; the id is preserved here as run provenance.
    /// </summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>
    /// Optional <c>ModelCredential</c> this run authenticates with — a REFERENCE (id), never the key, so it's safe to
    /// freeze into the persisted task. Resolved by the dispatch-time <c>IAgentDefinitionResolver</c> with the precedence
    /// node-override &gt; persona default &gt; null; the executor decrypts it just-in-time and injects it into the sandbox
    /// env, never persisting the secret. Null = fall back to a team default / operator-global key at resolve time.
    /// </summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>
    /// Optional reference to a SPECIFIC credentialed model — a <c>ModelCredentialModel</c> row that is a model id
    /// PAIRED with its backing credential. When set, the dispatch-time <c>IAgentDefinitionResolver</c> EXPANDS it into
    /// <see cref="Model"/> + <see cref="ModelCredentialId"/> from that row (the operator picked one concrete model from
    /// a credential's list), taking node-level precedence over a persona's defaults. Null (the default) → the loose
    /// <see cref="Model"/> + <see cref="ModelCredentialId"/> path, byte-identical to before. Carried as provenance after
    /// expansion. <c>[JsonIgnore(WhenWritingNull)]</c> so an unset reference adds nothing to the persisted task_json.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ModelCredentialModelId { get; init; }

    /// <summary>
    /// Tool allow-list the harness restricts the agent to — null = the harness's default toolset, empty = no tools,
    /// non-empty = exactly these. A harness that supports allow-lists projects it (Claude Code → <c>--allowed-tools</c>);
    /// one that doesn't (Codex, which restricts via sandbox) carries it without enforcement. Resolved from the persona's
    /// tools UNIONed with any node-level tools by the dispatch-time resolver.
    /// </summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>
    /// Skills the agent carries — resolved from the persona's bindings by the dispatch-time resolver and frozen here,
    /// then projected by the harness into its native <c>SKILL.md</c> layout under the per-run config home (the CLI does
    /// the progressive disclosure). <c>null</c> (default) → no skills, byte-identical to a pre-field task envelope;
    /// <c>[JsonIgnore(WhenWritingNull)]</c> so an unset list adds nothing to the persisted task_json.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AgentSkill>? Skills { get; init; }

    /// <summary>Sandbox runner to execute on — e.g. "local", "docker", "k8s". Null → the executor's default. The knob for choosing / overriding the execution backend per run.</summary>
    public string? RunnerKind { get; init; }

    /// <summary>Bound repository the agent works in — the executor clones it into the workspace before running the harness. Null → no workspace (analysis-only / no-repo run). One workspace source among future others (raw URL, upstream-produced); all resolve to a <c>WorkspaceRequest</c>. The legacy single-repo field — superseded by <see cref="Workspace"/>, kept for back-compat (a null <see cref="Workspace"/> derives a single-repo workspace from this).</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>The authored multi-repo workspace (multi-repo PR1) — WHICH repos the agent works across + each repo's access + the cwd mode. Null (the default) derives a single-repo workspace from <see cref="RepositoryId"/>, so an existing run is byte-identical. When set, it is the canonical workspace intent and <see cref="RepositoryId"/> is ignored.</summary>
    public WorkspaceSpec? Workspace { get; init; }

    /// <summary>Isolated working directory the agent runs in (the executor prepares it from <see cref="RepositoryId"/>). Null → the runner's default.</summary>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>
    /// The single named autonomy tier chosen for this run — the one axis an operator sets. <see cref="Permissions"/>
    /// is DERIVED from it (via <c>AgentAutonomyPolicy</c>) and may then be overridden per-field. Carried as provenance
    /// so the run's intent is auditable independently of the concrete knobs.
    /// </summary>
    public AgentAutonomyLevel Autonomy { get; init; } = AgentAutonomyLevel.Standard;

    /// <summary>What the agent is allowed to do — mapped by the harness onto its sandbox flags. Derived from <see cref="Autonomy"/> plus any per-field overrides.</summary>
    public AgentPermissions Permissions { get; init; } = new();

    /// <summary>Extra environment for the agent process (short-lived credentials are injected here by AgentRunService, then wiped).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Wall-clock cap for the whole agent run, in seconds. Default 3600 (1h). <c>null</c> ⇒ NO wall-clock (unbounded) — the run is then bounded only by the stall watchdog (no-progress) + the cost cap; use for a genuinely long task. Never defaulted to null: an absent value falls back to the bounded default, so only an EXPLICIT null (the operator's "no timeout" choice) is unbounded.</summary>
    public int? TimeoutSeconds { get; init; } = 3600;

    /// <summary>The conversation a run posts its tool-approval cards into — null = no approval surface (which fails closed in a later slice). Stored only; nothing reads it yet.</summary>
    public Guid? ApprovalConversationId { get; init; }

    /// <summary>
    /// Per-run opt-in to the in-process MCP tool-fabric endpoint, layered OVER the deployment-wide
    /// <c>CODESPACE_AGENT_MCP_ENDPOINT_ENABLED</c> env flag: <c>true</c> forces the endpoint open for THIS run even
    /// when the ambient flag is off; <c>null</c> (default) defers to the ambient flag, so an ordinary run is unchanged.
    /// The executor's single gate ORs the two (<c>AgentRunExecutor.ShouldOpenMcpEndpoint</c>), so two runs in the SAME
    /// process can genuinely differ on whether the fabric is reachable — which is what the benchmark instrument's
    /// CLI-vs-CLI+MCP comparison requires (one mode sets this true, the other leaves it null). There is no per-run way
    /// to force the endpoint OFF when the ambient flag is on — fail-open toward the operator's deployment intent.
    /// </summary>
    public bool? EnableMcpEndpoint { get; init; }

    /// <summary>
    /// Per-run opt-in to pushing the run's diff to a produced branch, layered OVER the deployment-wide
    /// <c>CODESPACE_AGENT_PUSH_BRANCH_ENABLED</c> env flag (the SAME OR-gate shape as <see cref="EnableMcpEndpoint"/>):
    /// <c>true</c> pushes a branch for THIS run even when the ambient flag is off; <c>null</c> (default) defers to the
    /// ambient flag, so an ordinary run is unchanged. The executor's single gate ORs the two
    /// (<c>AgentRunExecutor.ShouldPushProducedBranch</c>). This is the knob the one-agent-one-branch fan-out sets so each
    /// branch agent publishes its own branch (the universal multi-agent primitive) WITHOUT flipping the global flag for
    /// every run. There is no per-run way to force push OFF when the operator enabled it deployment-wide — fail-open
    /// toward the operator's intent, exactly like the MCP gate.
    /// </summary>
    public bool? PushProducedBranch { get; init; }
}

/// <summary>What the agent may do — the declarative half of the sandbox policy a harness maps to its flags.</summary>
public sealed record AgentPermissions
{
    public AgentNetworkAccess Network { get; init; } = AgentNetworkAccess.Off;

    public AgentWriteScope WriteScope { get; init; } = AgentWriteScope.Workspace;

    /// <summary>
    /// Egress posture WHEN <see cref="Network"/> is On: <see cref="AgentEgressPolicy.Full"/> (the default — reach any
    /// host, today's behaviour) or <see cref="AgentEgressPolicy.Allowlist"/> (deny-by-default; reachable = the run's
    /// model-API host + git host(s) + <see cref="EgressAllowHosts"/>, enforced by the sandbox's filtered netns and
    /// FAIL-CLOSED on a runner that can't enforce it). Network Off ⇒ no egress regardless of this. <c>[JsonIgnore(WhenWritingDefault)]</c>
    /// so a Full (default) run's permissions serialize byte-identically to a pre-field envelope.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public AgentEgressPolicy Egress { get; init; } = AgentEgressPolicy.Full;

    /// <summary>
    /// Operator-configured EXTRA hosts reachable under <see cref="AgentEgressPolicy.Allowlist"/> egress (package
    /// registries, internal services) — UNIONed with the auto-derived model + git hosts. Null/empty under Allowlist ⇒
    /// only model + git are reachable. Ignored under <see cref="AgentEgressPolicy.Full"/>. <c>[JsonIgnore(WhenWritingNull)]</c>
    /// so an unset list adds nothing to the persisted permissions.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EgressAllowHosts { get; init; }
}

public enum AgentNetworkAccess
{
    Off,
    On,
}

/// <summary>
/// The egress posture for a networked run — a governance knob that hangs off <see cref="AgentPermissions"/> per the
/// autonomy axis. Default <see cref="Full"/> keeps every existing run byte-identical; <see cref="Allowlist"/> is the
/// opt-in deny-by-default mode the sandbox enforces via its filtered network namespace.
/// </summary>
public enum AgentEgressPolicy
{
    /// <summary>Reach any host (today's behaviour when network is On).</summary>
    Full,

    /// <summary>Deny-by-default: reachable hosts = the run's model-API host + git host(s) + <see cref="AgentPermissions.EgressAllowHosts"/>, enforced by the sandbox's filtered netns. Fail-closed on a runner that can't enforce (severed, never widened).</summary>
    Allowlist,
}

/// <summary>
/// The single named autonomy axis — ascending capability, each tier DERIVING a default <see cref="AgentPermissions"/>
/// (see <c>AgentAutonomyPolicy</c>). Replaces scattered network/read-only toggles with one operator-legible dial;
/// future governance knobs (network allowlist, side-effect approval, privileged runner) hang off the SAME axis
/// without widening call sites. Per-field overrides may still be layered on top of the derived defaults.
/// </summary>
public enum AgentAutonomyLevel
{
    /// <summary>Analysis-only: may not write, no network — the most restricted tier.</summary>
    Confined,

    /// <summary>May write inside its workspace, no network. The safe default (matches the historical permission default).</summary>
    Standard,

    /// <summary>Workspace write + network — for runs that fetch dependencies or call out.</summary>
    Trusted,

    /// <summary>Highest capability (admin / controlled runners only). Today the same concrete knobs as <see cref="Trusted"/>; diverges as privileged-runner / no-approval axes are added.</summary>
    Unleashed,
}

public enum AgentWriteScope
{
    /// <summary>May only write inside its workspace directory.</summary>
    Workspace,

    /// <summary>May not write at all (analysis-only).</summary>
    ReadOnly,
}
