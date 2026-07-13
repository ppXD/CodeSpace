using System.Text.Json.Serialization;
using CodeSpace.Messages.Enums;

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
    /// <summary>Natural-language goal / prompt for the agent — the TASK (the user turn), never the persona.</summary>
    public required string Goal { get; init; }

    /// <summary>
    /// The CLEAN task text for display (an agent card's title), distinct from <see cref="Goal"/> — a CONTINUE's Goal
    /// is prefixed with the session's grounding digest ("# Earlier turns…") before it reaches the model, so deriving
    /// a title from Goal's first line would show the grounding heading instead of what the user actually asked. Null
    /// on older/legacy task envelopes (pre-dating this field) ⇒ the reader falls back to Goal's first line.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayTitle { get; init; }

    /// <summary>
    /// B1: the agent's PERSONA / system prompt (its identity + standing instructions), distinct from the task <see cref="Goal"/>.
    /// The resolver stamps it from the bound persona; each harness projects it through its NATIVE system-prompt channel
    /// (Claude Code: <c>--append-system-prompt</c>; Codex: an <c>AGENTS.md</c> in its config home), NOT prepended to the
    /// goal — Anthropic's own guidance is that a system-prompt persona outweighs the same text in the user message. Null
    /// (an inline run with no persona) ⇒ only the always-on operating contract is injected; task_json byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemPrompt { get; init; }

    /// <summary>Harness kind to run this task — resolved via <see cref="IAgentHarnessRegistry"/> (e.g. "codex-cli").</summary>
    public required string Harness { get; init; }

    /// <summary>Model id within the chosen harness's <see cref="IAgentHarness.Models"/> catalog, or null/blank to let the harness pick its own default (the Model=empty rule).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// P3.2: the prior CLI session/thread id this run CONTINUES — the harness threads it back as <c>--resume &lt;id&gt;</c>
    /// (Claude) / <c>exec resume &lt;id&gt;</c> (Codex) so a re-staged agent picks up its earlier conversation instead of
    /// cold-starting. Null (the default) ⇒ a fresh run, argv byte-identical to a pre-field envelope. The re-stage
    /// decision that SETS this (from the prior run's captured <see cref="AgentRunResult.SessionId"/>) is a later slice;
    /// <c>[JsonIgnore(WhenWritingNull)]</c> so an unset hint adds nothing to the persisted task_json.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResumeFromSessionId { get; init; }

    /// <summary>
    /// P3.3a: the prior session's captured transcript bytes (the raw CLI JSONL) this run RESTORES so the agent's
    /// <c>--resume</c> finds its earlier conversation. The harness lays it down as a config-home file where the CLI
    /// reads it (Claude: <c>projects/&lt;sanitized-cwd&gt;/&lt;ResumeFromSessionId&gt;.jsonl</c>). Null (the default) ⇒
    /// no transcript restored, byte-identical. Set together with <see cref="ResumeFromSessionId"/> by the CONTINUE
    /// re-stage. Carries the bytes INLINE only when the prior transcript was small (below the artifact inline
    /// threshold); a large one rides <see cref="RestoredTranscriptArtifactId"/> instead (a REF, kept out of task_jsonb)
    /// and the executor resolves it to these bytes just before invocation. <c>[JsonIgnore(WhenWritingNull)]</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestoredTranscript { get; init; }

    /// <summary>
    /// P3 (3.2c): a REFERENCE to the prior session transcript in the artifact store, used INSTEAD of inlining a large
    /// transcript into <see cref="RestoredTranscript"/> (which would bloat task_jsonb unboundedly and, on a continue
    /// CHAIN, trend to O(N²)). The producer stamps this ref; the executor resolves it to bytes into
    /// <see cref="RestoredTranscript"/> just before <c>BuildInvocation</c>, so the harness stays a pure bytes consumer.
    /// Null (the default) ⇒ nothing to resolve. <c>[JsonIgnore(WhenWritingNull)]</c> so an unset ref adds nothing to task_json.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RestoredTranscriptArtifactId { get; init; }

    /// <summary>
    /// P3 (D1): the supervisor SUBTASK id this agent was spawned for — the linking key for retry-resume. When the
    /// supervisor RETRIES a subtask, the producer finds the prior attempt at the SAME subtask in the same run and
    /// resumes its conversation. Only the supervisor's spawn/retry stamps it; a top-level agent.run run leaves it null
    /// (its continuity keys on the fork-cell lineage instead). <c>[JsonIgnore(WhenWritingNull)]</c> so a non-supervisor
    /// task's task_json is byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubtaskId { get; init; }

    /// <summary>
    /// P1 identity: WHICH durable plan row this attempt was dispatched under (<c>WorkPlan.Id</c>), stamped at the
    /// supervisor's staging chokepoint. With <see cref="PlanVersion"/> + <see cref="SubtaskId"/> it is the
    /// tape-archaeology-free source of a receipt's <c>WorkUnitRef</c> — a superseded plan's attempt never has to be
    /// re-derived by ordering plan decisions against origin keys. Null for a plan-less dispatch (a direct
    /// agent.run, a spawn before any plan persisted); null-omitted so every other lane's task_json is byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? WorkPlanId { get; init; }

    /// <summary>The plan VERSION this attempt was dispatched under — see <see cref="WorkPlanId"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanVersion { get; init; }

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
    /// Per-run OPT-OUT of pushing the run's diff to a produced branch — push is DEFAULT-ON for a non-empty diff
    /// (the publish guard chain, <c>IPublishGuard</c>, is the explicit opt-out surface: <c>false</c> trips the
    /// <c>ProfileOptOutPublishGuard</c>; <c>null</c>/<c>true</c> defer to the rest of the chain — no token, or the
    /// repo's own <c>PublishMode</c>, may still skip the push). This is the knob the one-agent-one-branch fan-out
    /// leaves unset so each branch agent publishes its own branch (the universal multi-agent primitive) by default.
    /// </summary>
    public bool? PushProducedBranch { get; init; }

    /// <summary>
    /// Per-run opt-in to reviewing the agent's OUTPUT (its produced change) with an INDEPENDENT critic at completion.
    /// <see cref="ReviewMode.None"/> (the default) ⇒ no review (byte-identical). GATE: a disapproved change re-grades a
    /// would-be <c>Succeeded</c> run to <see cref="AgentRunStatus.NeedsReview"/> so a human looks before the downstream
    /// PR-open (which only proceeds on Succeeded) consumes it — the captured work is preserved. IMPROVE (S6): the same
    /// review, but a disapproval first buys the agent a bounded revise round (the critique fed back into the same
    /// conversation) before the flag stands. <c>[JsonIgnore(WhenWritingDefault)]</c> so an unconfigured task's persisted
    /// task_json is byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ReviewMode OutputReviewMode { get; init; } = ReviewMode.None;

    /// <summary>
    /// THIS task's OBJECTIVE definition-of-done (triad S5 — the sprint-contract carried to the single agent): a
    /// server-run check the executor grades against the produced branch at completion, fail-closed — a failing
    /// oracle re-grades the run to Failed ("acceptance-failed"), so success can never be a model self-report.
    /// Sources: the plan item's authored acceptance (plan-map branches bind it per item) or the operator's
    /// quick-tier checks floor. Null ⇒ no oracle (byte-identical). Supervisor-dispatched units deliberately do
    /// NOT carry it — their per-unit gate grades at the fold (one grade, not two).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SupervisorAcceptanceSpec? Acceptance { get; init; }

    /// <summary>
    /// S2 — whether THIS task is expected to produce a code diff/branch at all. Meaningless without
    /// <see cref="Acceptance"/> (a task with no contract is never graded regardless). Null defaults to <c>true</c>
    /// (<c>AgentAcceptanceContract.ExpectsChanges</c>, Core) — byte-identical fail-closed on a missing branch/repo.
    /// Set <c>false</c> for a check that verifies something OTHER than a diff (e.g. an investigation report), so a
    /// legitimately branch-less run is graded against its recorded patch (if one exists) or treated as
    /// not-applicable (never penalized) instead of unconditionally failing closed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExpectsChanges { get; init; }

    /// <summary>The credentialed-model ROW the output critic runs on. Null ⇒ the critic auto-picks the team's strongest structured-eligible model. Only consulted when <see cref="OutputReviewMode"/> is not None. <c>[JsonIgnore(WhenWritingNull)]</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReviewerModelId { get; init; }

    /// <summary>
    /// S8: review this run's output with a REAL independent agent (a read-only run cloning the produced branch on a
    /// DIFFERENT harness when one is registered) instead of only the in-process model critic — the executor ladders
    /// agent → model critic → fail-open, so an agent review is never worse. Only consulted when
    /// <see cref="OutputReviewMode"/> is not None. The reviewer's own task pins this false (recursion-proof).
    /// <c>[JsonIgnore(WhenWritingDefault)]</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReviewerAgent { get; init; }

    /// <summary>
    /// S6: how many bounded REVISE rounds the executor may run inside this run when the objective oracle fails or the
    /// Improve-mode critic flags the output — each round feeds the failure detail back to the SAME agent (a same-session
    /// harness continuation in the same workspace) and re-verifies through the full push→grade→review chain. Null ⇒ the
    /// executor's default: 1 round under <see cref="ReviewMode.Improve"/> (Improve MEANS improve), else 0 (S5's hard-gate
    /// semantics, byte-identical). Clamped server-side. Each round re-arms <see cref="TimeoutSeconds"/>, so the run's
    /// wall-clock ceiling is (1 + rounds) × timeout. Supervisor-dispatched units pin this to an EXPLICIT 0 — the
    /// supervisor's own retry loop owns their revision (a null would let Improve imply an in-run round and stack the
    /// two loops). <c>[JsonIgnore(WhenWritingNull)]</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxReviseRounds { get; init; }
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
