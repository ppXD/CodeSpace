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
  maxRounds?: number;
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
  /** A tighten-only autonomy ceiling (a tier name) the run's agents may not exceed — binds into
   *  `LaunchTaskCommand.AutonomyCeiling`, merged onto the effort preset's ceiling (can only lower it). Absent /
   *  "" = inherit the preset. Sent only on deep/auto (the Coordination tab that sets it). */
  autonomyCeiling?: string;
  /** Deep-only: opt in to integrating the spawned agents' diffs into one reviewable branch at merge. Omitted ⇒ defer to the ambient flag. */
  integrateBranches?: boolean;
  /** Deep-only: free-text acceptance criteria the supervisor targets (rendered into its prompt, never executed). Omitted when unchanged from the default. */
  acceptanceCriteria?: string[];
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
  /** How an independent critic reviews each agent's output (`"Gate"`). Omitted (no review) when None. */
  outputReviewMode?: string;
  /** The credentialed-model ROW id the critic(s) run on. Omitted ⇒ auto-pick. Only sent when a review is active. */
  reviewerModelId?: string;
}

/** Mirror of the backend `LaunchTaskResult` — only the fields the UI consumes. `runId` is the
 *  started snapshot run's id (always set; the launch always runs); the caller navigates to it. */
export interface LaunchTaskResult {
  runId: string;
  projectionKind: string;
  surfaceKind: string;
}

export const tasksApi = {
  // Launch a run from a task spec — the run resource is rooted at api/workflows/runs (the substrate is the
  // workflow engine), so launching a task is creating a run.
  launch: (input: LaunchTaskInput) =>
    fetchJson<LaunchTaskResult>("/api/workflows/runs", { method: "POST", body: JSON.stringify(input) }),
};
