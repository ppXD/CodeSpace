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
  relatedRepositories?: LaunchRelatedRepository[];
  caps?: LaunchCaps;
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
