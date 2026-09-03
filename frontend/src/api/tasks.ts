import { fetchJson } from "./request";

/** The surfaces the generic launch modal can be opened from — the SEEDABLE subset of the backend
 *  `TaskLaunchSurfaceKinds` consts. `chat` and `repo` have live seed providers; the backend's
 *  reserved `pr` / `issue` / `project` are deliberately EXCLUDED here because they have no provider
 *  yet — launching one would throw in `TaskLaunchSeedProviderRegistry.Resolve` (a 500), so the type
 *  forbids opening the modal with one. Add a surface back when its provider lands. */
export type TaskSurfaceKind = "chat" | "repo";

/** One non-primary repo in a multi-repo launch — mirrors the backend `TaskRelatedRepository` noun.
 *  `access` is `"write"`/`"read"`; a blank `alias` is omitted (the backend derives one). */
export interface LaunchRelatedRepository {
  repositoryId: string;
  alias?: string;
  access?: string;
}

/** The operator's optional safety-budget caps — mirrors the backend `TaskCapsOverride` noun. Every cap
 *  is optional: a set value replaces the effort preset's, an omitted one keeps the preset default. Bounds
 *  a fan-out / supervisor loop, so it is inert on a single-agent (quick) run. */
export interface LaunchCaps {
  maxCostUsd?: number;
  maxParallelism?: number;
  maxTotalSpawns?: number;
}

/**
 * The WIRED subset of `LaunchTaskCommand` the modal sends. Optional fields are omitted (sent null/absent)
 * when the operator leaves them on their default so the backend's projection picks the smart default —
 * sending a blank string would override it. `relatedRepositories` (multi-repo workspace) and `caps`
 * (Coordination Limits / Budget) bind straight into the existing `LaunchTaskCommand.RelatedRepositories`
 * / `Caps` seams. The supervisor model/pool/acceptance and the per-run profile toggles are still
 * design-ahead and intentionally absent from this shape until their backend seams land.
 */
export interface LaunchTaskInput {
  taskText: string;
  surfaceKind: TaskSurfaceKind;
  /** Continue an existing work session as its NEXT top-level turn. Binds to `LaunchTaskCommand.SessionId`
   *  (→ `ContinueSessionId`); omitted ⇒ a fresh session is opened. The session view's composer sets this. */
  sessionId?: string | null;
  repositoryId?: string | null;
  baseBranch?: string | null;
  effort?: string | null;
  autonomy?: string | null;
  harness?: string | null;
  model?: string | null;
  agentDefinitionId?: string | null;
  runnerKind?: string | null;
  modelCredentialId?: string | null;
  /** A picked credentialed-model ROW id (`ModelCredentialModel` id) — the operator's one (model, credential) choice.
   *  On a Deep launch it pins the supervisor BRAIN; on single-agent it pins the agent model. Omitted ⇒ the loose
   *  `model` / `modelCredentialId` ⇒ auto. Takes precedence over those loose fields when present. */
  modelCredentialModelId?: string | null;
  /** The agent run's wall-clock cap, in seconds. Omitted ⇒ the backend's bounded 1h default. 0 ⇒ NO wall-clock (unbounded — bounded only by the stall watchdog + cost cap). */
  timeoutSeconds?: number | null;
  relatedRepositories?: LaunchRelatedRepository[];
  caps?: LaunchCaps;
  /** The allowed model pool for a Deep run's dispatched agents — credentialed-model ROW ids (not names). Binds
   *  into `LaunchTaskCommand.AllowedModelIds`; the backend validates each row is team-owned. Empty/absent = all
   *  the team's models. Sent only on deep/auto (the supervisor pool is inert on a single-agent run). */
  allowedModelIds?: string[];
  /** The allowed agent (persona) pool for a Deep run's dispatched agents — AgentDefinition ROW ids. Empty/absent = all the team's personas. Sent only on deep/auto. */
  allowedAgentDefinitionIds?: string[];
  /** A tighten-only autonomy ceiling (a tier name) the run's agents may not exceed — binds into
   *  `LaunchTaskCommand.AutonomyCeiling`, merged onto the effort preset's ceiling (can only lower it). Absent /
   *  "" = inherit the preset. Sent only on deep/auto (the Coordination tab that sets it). */
  autonomyCeiling?: string;
  /** Deep-only: opt in to integrating the spawned agents' diffs into one reviewable branch at merge. Omitted ⇒ defer to the ambient flag. */
  integrateBranches?: boolean;
  /** Deep-only: free-text acceptance criteria the supervisor targets (rendered into its prompt, never executed). Omitted when unchanged from the default. */
  acceptanceCriteria?: string[];
  /** Deep-only: every authored plan version parks for the operator's confirmation before any agent runs (S3 gate; the launch stages the session's channel as the card surface). Omitted ⇒ fully autonomous planning. */
  requirePlanConfirmation?: boolean;
  /** Standard/Auto: how an independent critic reviews the AUTHORED PLAN (the plan.author reviewMode) — "Gate" / "Improve". Omitted ⇒ no critic. */
  plannerReviewMode?: string;
  /** The EXECUTABLE acceptance argv floor (e.g. ["sh","check.sh"]): Deep enforces it at the terminal stop; Quick grades the single agent's produced branch (S5). Omitted ⇒ no floor. */
  acceptanceChecks?: string[];
  /** Multi-repo working-directory mode (`"workspace"` / `"primary"`). Omitted for `"auto"` (the default). Inert on a single-repo run. */
  workingDirMode?: string;
  /** Per-run opt-in to the full (side-effecting) MCP tool fabric. Omitted (defer to the ambient flag) unless `true`. */
  enableMcp?: boolean;
  /** Claude-only tool allow-list (canonical names). Omitted ⇒ the harness default (all tools). Additive against a persona's tools; not a write boundary. */
  allowedTools?: string[];
  /** Per-run opt-in to publishing the agent's diff as a branch. Omitted (defer to the ambient flag) unless `true`. */
  pushBranch?: boolean;
  /** How an independent critic reviews each supervisor decision (`"Gate"`/`"Improve"`). Omitted (no review) when None. Deep only. */
  decisionReviewMode?: string;
  /** How an independent critic reviews each agent's output (`"Gate"` / `"Improve"` — Improve feeds the critique back for a bounded self-revision). Omitted (no review) when None. */
  outputReviewMode?: string;
  /** The credentialed-model ROW id the critic(s) run on. Omitted ⇒ auto-pick. Only sent when a review is active. */
  reviewerModelId?: string;
  /** S6: how many self-revise rounds an agent gets when its acceptance check fails or the Improve critic flags it (0 disables — even Improve's implied round). Omitted ⇒ the backend default (1 under Improve, else 0). Clamped server-side. */
  reviseRounds?: number;
  /** S8: review each agent's output with a REAL independent agent (read-only clone of the produced branch, prefers a different harness; the model critic is the fallback). Omitted ⇒ the model critic. */
  reviewerAgent?: boolean;
  /** P3.2: the QUALITY tier this launch MANDATES (`"Delivery"` / `"Unattended"`) — the backend enforces it server-side (an executable `acceptanceChecks` floor on a Deep launch, an `outputReviewMode` floor), so a caller can't claim Delivery/Unattended while skipping the knobs that actually gate it. Omitted ⇒ Prototype (self-report only, byte-identical to before this field existed). */
  tier?: string;
}

/** Mirror of the backend `LaunchTaskResult` — only the fields the UI consumes. `runId` is the
 *  started snapshot run's id (always set; the launch always runs); the caller navigates to it. */
export interface LaunchTaskResult {
  runId: string;
  projectionKind: string;
  surfaceKind: string;
}

/** One compiled suggestion set from the spec-preview lane (P5-7) — mirrors the backend `TaskSpecSuggestion`.
 *  Every field maps onto an EXISTING launch field; these are editable proposals, never stakes. `openPullRequest` /
 *  `targetBranch` ride along for when the modal wires DeliverySpec — the card ignores them until then. */
export interface TaskSpecSuggestion {
  acceptanceChecks: string[];
  acceptanceCriteria: string[];
  openPullRequest?: boolean | null;
  targetBranch?: string | null;
  rationale: string;
  confidence: number;
}

/** Mirror of the backend `CompileTaskSpecResult`. A null/absent `suggestion` is the documented degrade
 *  (no structured model / model-path miss) — the composer renders nothing, never an empty scaffold. */
export interface CompileTaskSpecResult {
  suggestion?: TaskSpecSuggestion | null;
  grounded: boolean;
}

/** One selectable effort tier on a route confirm card — mirrors the backend `ConfirmCardOption`. `mode` is the
 *  open effort string sent back as the launch's EXPLICIT `effort`, which short-circuits the classifier. */
export interface RouteConfirmOption {
  mode: string;
  label: string;
  hint?: string | null;
}

/** Mirror of the backend `ConfirmCard`. The router builds one whenever an auto route landed below its confidence
 *  floor OR the classifier flagged risky side effects — the options are derived from the live bounds presets, so
 *  the composer must render whatever comes back rather than a hardcoded tier list. */
export interface RouteConfirmCard {
  suggestedMode: string;
  rationale: string;
  options: RouteConfirmOption[];
}

/** The generic signals the classifier extracted — mirrors the backend `EffortSignals`. Only `riskySideEffects` is
 *  read by the composer (it earns the risk badge); the rest ride along for future surfacing. */
export interface RouteSignals {
  needsCodeChange?: boolean;
  crossFile?: boolean;
  needsTestsOrCi?: boolean;
  riskySideEffects?: boolean;
  ambiguous?: boolean;
  estimatedCostTier?: string;
}

/** Mirror of the backend `EffortDecision` — the classifier's own output, kept distinct from the router's verdict. */
export interface RouteDecision {
  signals: RouteSignals;
  suggestedEffort: string;
  suggestedRecipe: string;
  confidence: number;
  rationale: string;
  classifierKind: string;
}

/** Mirror of the backend `RoutePlan` — the routing decision a launch would (or did) run under. `needsConfirmCard`
 *  + `confirm` are the load-bearing pair: before B1 the backend built this card and nothing ever showed it, so a
 *  risky auto-classified task started with no human gate at all. */
export interface RoutePlan {
  effortMode: string;
  recipeKind: string;
  projectionKind: string;
  boundsPreset: string;
  recommendedAutonomy: string;
  needsConfirmCard: boolean;
  needsPlanReview: boolean;
  wasAutoClassified: boolean;
  classifierConfidence: number;
  degradedReason?: string | null;
  decision?: RouteDecision | null;
  confirm?: RouteConfirmCard | null;
}

/** Mirror of the backend `TaskRoutePreviewResult`. */
export interface TaskRoutePreviewResult {
  route: RoutePlan;
}

/** The WIRED subset of `PreviewTaskRouteCommand` — every field that actually changes the router's answer, and
 *  nothing else. Built ONLY by `buildRoutePreviewInput`, which derives it from the launch input so the previewed
 *  route is the route the launch would take. */
export interface RoutePreviewInput {
  taskText: string;
  surfaceKind: TaskSurfaceKind;
  repositoryId?: string;
  relatedRepositories?: LaunchRelatedRepository[];
  baseBranch?: string;
  effort?: string;
  caps?: LaunchCaps;
  autonomyCeiling?: string;
}

export const tasksApi = {
  // Launch a run from a task spec — the run resource is rooted at api/workflows/runs (the substrate is the
  // workflow engine), so launching a task is creating a run.
  launch: (input: LaunchTaskInput) =>
    fetchJson<LaunchTaskResult>("/api/workflows/runs", { method: "POST", body: JSON.stringify(input) }),
  // Compile a free-text goal into launch-contract suggestions (read-only; nothing persisted, nothing staked).
  specPreview: (input: { goal: string; repositoryId?: string }) =>
    fetchJson<CompileTaskSpecResult>("/api/workflows/runs/spec-preview", { method: "POST", body: JSON.stringify(input) }),
  // Preview the ROUTE a launch would take — same seed provider, same router, same request mapping as the launch
  // itself. Read-only: no session is opened, no run is staged, nothing is persisted.
  routePreview: (input: RoutePreviewInput) =>
    fetchJson<TaskRoutePreviewResult>("/api/workflows/runs/route-preview", { method: "POST", body: JSON.stringify(input) }),
};
