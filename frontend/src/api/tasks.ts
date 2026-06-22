import { fetchJson } from "./request";

/** The surfaces the generic launch modal can be opened from — mirror of the backend
 *  `TaskLaunchSurfaceKinds` consts. Only `chat` has a live seed provider today; the rest
 *  pin context but route through the same free-text path. */
export type TaskSurfaceKind = "chat" | "pr" | "issue" | "project" | "repo";

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
  launch: (input: LaunchTaskInput) =>
    fetchJson<LaunchTaskResult>("/api/tasks", { method: "POST", body: JSON.stringify(input) }),
};
