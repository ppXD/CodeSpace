using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Messages.Commands.Tasks;

/// <summary>
/// The GENERIC L1 task-launch command — the single entry that ties seed → route → project → snapshot → run into
/// one call (PR4). Dispatched at <c>POST /api/tasks</c>; the thin handler folds it into a <c>TaskLaunchRequest</c>
/// and delegates to <c>ITaskLaunchService</c>, which ALWAYS produces a running snapshot run (no Workflow row, no
/// feature flag — the endpoint is live).
///
/// <para>Tenancy: <see cref="IRequireTeamMembership"/>; the team is resolved from <c>ICurrentTeam</c> and the actor
/// from <c>ICurrentUser</c> in the handler — NEVER this body (fail-closed). A <see cref="RepositoryId"/> is validated
/// TEAM-SCOPED by the service; a repo outside the team is a clear not-found, never a cross-team leak.</para>
///
/// <para><see cref="Effort"/> / <see cref="Autonomy"/> / <see cref="Harness"/> / <see cref="RunnerKind"/> /
/// <see cref="SurfaceKind"/> are OPEN STRINGS (no enum) — a new tier / surface is a new const, never a core-enum edit.
/// The operator's safety-budget caps (<see cref="Caps"/> — cost / parallelism / spawns / rounds) ride the router's
/// <c>CapsOverride</c> seam, which TIGHTENS the effort preset's caps (a cost cap force-stops the run via the
/// supervisor's bounds). ApprovalPolicy still rides <see cref="Autonomy"/> separately.</para>
/// </summary>
public sealed record LaunchTaskCommand : ICommand<LaunchTaskResult>, IRequireTeamMembership
{
    /// <summary>The operator's free-text task. Required by the chat surface; another surface may derive the goal from <see cref="LaunchContext"/> instead.</summary>
    public required string TaskText { get; init; }

    /// <summary>
    /// Continue an existing WorkSession: when set, this launch becomes the NEXT top-level turn of that thread
    /// (the run is bound to it) instead of opening a fresh one. Pass back the <c>SessionId</c> a prior launch
    /// returned. Validated TEAM-SCOPED + must be Open; a foreign / missing / archived session is rejected. Null
    /// (the default) opens a new session — byte-identical to the single-shot launch.
    /// </summary>
    public Guid? SessionId { get; init; }

    /// <summary>The repository the task targets, when named. Validated TEAM-SCOPED by the service; a foreign repo is a clear not-found.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>ADDITIONAL repositories to clone alongside the primary <see cref="RepositoryId"/> for a coordinated multi-repo change (e.g. a frontend + backend). EVERY entry is validated TEAM-SCOPED by the service (fail-closed, exactly like the primary). Null / empty ⇒ a single-repo run (byte-identical). Requires a primary <see cref="RepositoryId"/>.</summary>
    public IReadOnlyList<TaskRelatedRepository>? RelatedRepositories { get; init; }

    /// <summary>The base branch the work starts from, when named. Null → the repo's default.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>An operator-chosen effort tier (open string). Null / <c>"auto"</c> ⇒ the router classifies + rides a confirm card back.</summary>
    public string? Effort { get; init; }

    /// <summary>The autonomy tier the agent runs at (open string). Null / unrecognised → the safe <c>Standard</c> default.</summary>
    public string? Autonomy { get; init; }

    /// <summary>The harness the agent runs on (open string). Null / blank → the projection's harness default.</summary>
    public string? Harness { get; init; }

    /// <summary>The model id within the harness's catalog. Null / blank → the persona's model → the harness default.</summary>
    public string? Model { get; init; }

    /// <summary>The Agent persona (<c>AgentDefinition</c>) the agent embodies. Null → a pure-inline run.</summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>The sandbox runner the agent executes on (open string). Null → the executor's default.</summary>
    public string? RunnerKind { get; init; }

    /// <summary>The <c>ModelCredential</c> reference the agent authenticates with. Null → the persona default → the team/operator fallback.</summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>
    /// A picked credentialed-model ROW (<c>ModelCredentialModel</c> id) — the operator's one concrete (model, credential)
    /// choice from the Launch "Brain model" / "Agent model" chip. Sets both the model id and its backing credential from
    /// one pick (precedence over the loose <see cref="Model"/> / <see cref="ModelCredentialId"/>). On a Deep (supervisor)
    /// launch it pins the SUPERVISOR BRAIN; on single-agent it pins the agent's model. Null → the loose fields → defaults.
    /// </summary>
    public Guid? ModelCredentialModelId { get; init; }

    /// <summary>The agent run's wall-clock cap, in seconds. Null → the projection's bounded default (1h). 0 → NO wall-clock (unbounded — bounded only by the stall watchdog + cost cap). A positive value caps the run.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The operator's optional safety-budget caps (cost / parallelism / spawns / rounds). Null / empty ⇒ the effort preset's caps stand (byte-identical to no override). A set cap TIGHTENS the preset.</summary>
    public TaskCapsOverride? Caps { get; init; }

    /// <summary>
    /// The operator's ALLOWED MODEL POOL for the agents a Deep (supervisor) run dispatches — a multi-select of
    /// credentialed-model ROW ids (<c>ModelCredentialModel</c> ids, NOT model names). EVERY id is validated TEAM-SCOPED
    /// by the service (fail-closed, exactly like the repos): a foreign / disabled / deleted-credential row rejects the
    /// whole launch. Baked into the projected <c>agent.supervisor</c> node's <c>allowedModelIds</c>, where every
    /// dispatched agent's model must resolve to a row in the pool (out of pool ⇒ fails closed at dispatch). Null /
    /// empty ⇒ the pool is ALL the team's credentialed models (byte-identical to no pool). Inert on a non-supervisor
    /// projection (single-agent / map ignore it).
    /// </summary>
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }

    /// <summary>
    /// The operator's ALLOWED AGENT (persona) POOL for the agents a Deep (supervisor) run dispatches — a multi-select of
    /// <c>AgentDefinition</c> (persona) ROW ids, the persona analogue of <see cref="AllowedModelIds"/>. EVERY id is
    /// validated TEAM-SCOPED by the service (fail-closed, exactly like the repos / model pool): a foreign / deleted
    /// persona rejects the whole launch. Baked into the projected <c>agent.supervisor</c> node's
    /// <c>allowedAgentDefinitionIds</c>, where every dispatched agent's effective persona (model-authored slug OR the
    /// profile default) must be in the pool (out of pool ⇒ fails closed at dispatch). Null / empty ⇒ the pool is ALL
    /// the team's personas (byte-identical to no pool). Inert on a non-supervisor projection.
    /// </summary>
    public IReadOnlyList<Guid>? AllowedAgentDefinitionIds { get; init; }

    /// <summary>
    /// An operator-chosen autonomy CEILING (an open tier-name string, e.g. <c>"Standard"</c>) for the agents the run
    /// runs / dispatches — a TIGHTEN-ONLY bound the router merges onto the effort preset's ceiling (it can only LOWER
    /// it, never raise it), which then CLAMPS the run's autonomy via <c>ClampAutonomy</c>. Null / blank ⇒ inherit the
    /// preset's ceiling (byte-identical). An unrecognised tier safely degrades to "inherit the preset" (never removes
    /// the ceiling). Distinct from <see cref="Autonomy"/> (the run's REQUESTED tier — this caps what that may reach).
    /// </summary>
    public string? AutonomyCeiling { get; init; }

    /// <summary>Deep/supervisor only: per-run opt-in to INTEGRATING the spawned agents' diffs into one reviewable branch at merge (one PR over the combined work, instead of only the side-by-side fold). Null / false ⇒ defer to the ambient integrate flag (byte-identical). Inert on a single-agent / map projection.</summary>
    public bool? IntegrateBranches { get; init; }

    /// <summary>
    /// Deep/supervisor only: the operator's free-text ACCEPTANCE CRITERIA (e.g. "tests pass", "PR opened") — the
    /// definition of done the supervisor TARGETS. RENDERED into the supervisor decider prompt (a yardstick the model
    /// aims at); it is NOT executed and is DISTINCT from the executable <c>acceptanceChecks</c> argv floor. Null / empty
    /// ⇒ omitted ⇒ byte-identical. Inert on a non-supervisor projection. (The structured list is also the forward seam
    /// for a future supervisor critic-gate, which would judge against the same criteria.)
    /// </summary>
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>
    /// The S3 plan-confirmation gate: when true, every plan version the supervisor AUTHORS parks the run on a
    /// confirmation card before any agent is created — approve releases execution, any other answer is revision
    /// feedback folded into a revised plan version. Null / false ⇒ omitted ⇒ byte-identical (fully autonomous
    /// planning, the pre-S3 default). Inert on a non-supervisor projection.
    /// </summary>
    public bool? RequirePlanConfirmation { get; init; }

    /// <summary>How an INDEPENDENT critic reviews the AUTHORED PLAN on the plan-map tiers (S4b): None (default, byte-identical) / Gate (annotate concerns onto the plan's risks) / Improve (one bounded revision against the critique). Runs on <see cref="ReviewerModelId"/> when set. Inert on non-plan-map projections.</summary>
    public ReviewMode PlannerReviewMode { get; init; } = ReviewMode.None;

    /// <summary>The operator's EXECUTABLE acceptance floor (S4b, Deep only) — an argv (e.g. ["sh","check.sh"]) run against the run's reviewable head at the terminal stop; a non-zero exit fails the stop and withholds the branch. DISTINCT from the free-text <see cref="AcceptanceCriteria"/> (prompt-rendered, never executed). Null / empty ⇒ omitted ⇒ byte-identical. Inert on a non-supervisor projection.</summary>
    public IReadOnlyList<string>? AcceptanceChecks { get; init; }

    /// <summary>
    /// The agent working-directory mode in a MULTI-repo workspace, in wire vocabulary: <c>"workspace"</c> (cwd = the
    /// shared workspace root, every repo a sibling) or <c>"primary"</c> (cwd = the primary repo's root). <c>"auto"</c> /
    /// null ⇒ the Auto default (repo-root for one repo, workspace-root for many), omitted ⇒ byte-identical. INERT on a
    /// single-repo run (the single-repo invariant always runs at the repo root).
    /// </summary>
    public string? WorkingDirMode { get; init; }

    /// <summary>
    /// Per-run opt-in to the FULL MCP tool-fabric (the side-effecting catalog) for the run's agents. Null / false ⇒
    /// omitted ⇒ defer to the ambient deployment flag (the read-only catalog unless the deployment forces full) ⇒
    /// byte-identical. The gate is OR-only: this FORCES the full fabric ON for the run; it cannot turn it OFF when the
    /// deployment enabled it. Flows to both the single-agent <c>agent.code</c> node and each supervisor-spawned agent.
    /// </summary>
    public bool? EnableMcp { get; init; }

    /// <summary>
    /// The tool allow-list the run's agents are restricted to (canonical Claude tool names, e.g. <c>"Read"</c>,
    /// <c>"Grep"</c>, <c>"Bash"</c>). Null / empty ⇒ omitted ⇒ the harness default (all tools) ⇒ byte-identical. A
    /// CLAUDE-ONLY capability filter (Codex bounds the agent via its sandbox + autonomy, ignoring the list) and ADDITIVE
    /// against a persona's tools — NOT a write boundary (use the autonomy tier for that). Flows to both the single-agent
    /// <c>agent.code</c> node's <c>tools</c> and each supervisor-spawned agent's <c>allowedTools</c>.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>
    /// Per-run opt-in to PUBLISHING the run's agent diff(s) as a branch (<c>codespace/agent/&lt;runId&gt;</c>) even when
    /// the deployment-wide push flag is off. Null / false ⇒ omitted ⇒ defer to the ambient flag ⇒ byte-identical. An
    /// OR-gate: this FORCES publish ON for the run; it cannot force it OFF (the diff is captured for review regardless).
    /// Flows to both the single-agent <c>agent.code</c> node and each supervisor-spawned agent.
    /// </summary>
    public bool? PushBranch { get; init; }

    /// <summary>How an INDEPENDENT critic reviews each supervisor DECISION (Deep only) before its side effect — baked into the supervisor node. <see cref="ReviewMode.None"/> (the default) ⇒ no critic ⇒ byte-identical. Inert on a non-supervisor projection.</summary>
    public ReviewMode DecisionReviewMode { get; init; } = ReviewMode.None;

    /// <summary>How an INDEPENDENT critic reviews each agent's OUTPUT (its produced change) at completion. <see cref="ReviewMode.None"/> (the default) ⇒ no review ⇒ byte-identical. v1 supports Gate (a flagged change → NeedsReview). Flows to the single-agent <c>agent.code</c> node + each supervisor-spawned agent.</summary>
    public ReviewMode OutputReviewMode { get; init; } = ReviewMode.None;

    /// <summary>The credentialed-model ROW the review critic(s) run on — the shared reviewer for both the decision + output critics. Null ⇒ the critic auto-picks the team's strongest structured-eligible model. Only consulted when a review mode is not None.</summary>
    public Guid? ReviewerModelId { get; init; }

    /// <summary>The launch surface (an open <see cref="TaskLaunchSurfaceKinds"/> string). Defaults to <c>chat</c> — the registry resolves a seed provider by it.</summary>
    public string SurfaceKind { get; init; } = TaskLaunchSurfaceKinds.Chat;

    /// <summary>The opaque per-surface payload. The core never reads it — only the resolved seed provider does (the handler folds its <c>Raw</c> into the request's surface payload). Null for a free-text chat launch.</summary>
    public LaunchContext? LaunchContext { get; init; }
}

/// <summary>
/// The launch outcome (Rule 18.1, a pure data noun) — the <see cref="RunId"/> the caller tracks the run by, the
/// <see cref="ProjectionKind"/> that built it, the full <see cref="Route"/> (so the UI can show the
/// <c>NeedsConfirmCard</c> / <c>Confirm</c> escalation affordance — PR4 does NOT block on confirm; the operator
/// re-POSTs with an explicit <c>Effort</c> to change), the resolved <see cref="SurfaceKind"/>, and the linked entity
/// the seed carried. No <c>LaunchEnabled</c> / <c>WorkflowId</c> — there is no flag and no Workflow row.
/// </summary>
public sealed record LaunchTaskResult
{
    /// <summary>The <c>workflow_run.id</c> of the started snapshot run — ALWAYS set (the launch always runs).</summary>
    public required Guid RunId { get; init; }

    /// <summary>The <c>work_session.id</c> of the thread this launch opened — ALWAYS set; the run above is its first turn. A follow-up may continue the same session (a later slice).</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The projection kind that built the run — the open string the registry resolved a builder by.</summary>
    public required string ProjectionKind { get; init; }

    /// <summary>The routing decision the run was projected from — carries the confirm-card escalation affordance for the UI.</summary>
    public required RoutePlan Route { get; init; }

    /// <summary>The resolved launch surface (an open string) the seed came from.</summary>
    public required string SurfaceKind { get; init; }

    /// <summary>The external entity the task was launched from, when the seed carried one.</summary>
    public LinkedEntityRef? LinkedEntity { get; init; }
}
