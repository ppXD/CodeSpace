import { fetchJson } from "./request";

/** The surfaces the generic launch modal can be opened from — the SEEDABLE subset of the backend
 *  `TaskLaunchSurfaceKinds` consts. `chat` and `repo` have live seed providers; the backend's
 *  reserved `pr` / `issue` / `project` are deliberately EXCLUDED here because they have no provider
 *  yet — launching one would throw in `TaskLaunchSeedProviderRegistry.Resolve` (a 500), so the type
 *  forbids opening the modal with one. Add a surface back when its provider lands. */
export type TaskSurfaceKind = "chat" | "repo";

/**
 * The WIRED subset of `LaunchTaskCommand` the modal sends today. Optional fields are omitted
 * (sent null) when the operator leaves them on their default so the backend's projection picks
 * the smart default — sending a blank string would override it. Multi-repo, the supervisor
 * model/pool/bounds, and the per-run profile toggles are design-ahead and intentionally absent
 * from this shape until their backend seams land.
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
