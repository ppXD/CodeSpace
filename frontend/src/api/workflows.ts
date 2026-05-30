import { fetchJson } from "./request";

// ─── Types (mirror backend DTOs) ───────────────────────────────────────────────

export type NodeKind = "Regular" | "Trigger" | "Terminal";
export type NodeStatus = "Pending" | "Running" | "Success" | "Failure" | "Skipped" | "Suspended";
// Enqueued = dispatched, awaiting worker pickup. Still cancellable; no node activity yet.
// Suspended = paused on a node waiting for a timer / approval / callback; resumes on the signal.
export type WorkflowRunStatus = "Pending" | "Enqueued" | "Running" | "Success" | "Failure" | "Cancelled" | "Suspended";

/**
 * Open string instead of a closed enum. Examples: "manual", "replay", "schedule.cron",
 * "provider.github.pull_request". The UI renders the value as-is (it's already a stable,
 * namespaced identifier).
 */
export type WorkflowRunSourceType = string;

/** Mirrors WorkflowDefinition. Pure JSON — the editor reads/writes this shape directly. */
export interface WorkflowDefinition {
  schemaVersion: number;
  nodes: NodeDefinition[];
  edges: EdgeDefinition[];
  /** Per-run parameters. {{input.X}}. Manual run + HTTP trigger map values into these. */
  inputs?: WorkflowVariable[];
  /** Declared outputs the workflow emits via the Terminal node. {{output.X}}. */
  outputs?: WorkflowVariable[];

  // `variables` (wf.*) and `environment` (team.*) live in the unified `variable` table,
  // managed via /api/team-variables + /api/workflows/{id}/variables. The definition JSON
  // is pure graph + IO contract.
}

/**
 * Named, typed variable used across Variables / Inputs / Outputs. Schema is a JSON
 * Schema fragment — the editor renders an input from it via the same SchemaForm
 * component that drives node config/inputs forms.
 */
export interface WorkflowVariable {
  name: string;
  label?: string | null;
  description?: string | null;
  schema: unknown;
  default?: unknown;
  required?: boolean;
}

export interface NodeDefinition {
  id: string;
  typeKey: string;
  label?: string | null;
  /** Static design-time config object. Shape varies per node type. */
  config: unknown;
  /** Dynamic inputs — values can be literals, {{ref}} strings, or { "$ref": "..." } objects. */
  inputs: unknown;
  /** Canvas position (editor-only). When null the editor auto-lays out the node. */
  position?: NodePosition | null;
}

export interface NodePosition {
  x: number;
  y: number;
}

export interface EdgeDefinition {
  from: string;
  to: string;
  /** Source handle name for branch nodes (e.g. "true"/"false" for logic.if). */
  sourceHandle?: string | null;
  /** Target handle name for multi-input nodes. */
  targetHandle?: string | null;
  condition?: string | null;
}

export interface WorkflowSummary {
  id: string;
  teamId: string;
  name: string;
  description: string | null;
  enabled: boolean;
  latestVersion: number;
  createdDate: string;
  lastModifiedDate: string;
  /** The set of configured activation type-keys. */
  activationTypeKeys: string[];
}

export interface WorkflowDetail {
  id: string;
  teamId: string;
  name: string;
  description: string | null;
  enabled: boolean;
  latestVersion: number;
  definition: WorkflowDefinition;
  /** Configured run sources for this workflow. */
  activations: WorkflowActivationSummary[];
  createdDate: string;
  lastModifiedDate: string;
}

export interface WorkflowActivationSummary {
  id: string;
  typeKey: string;
  enabled: boolean;
  config: unknown;
}

export interface WorkflowActivationInput {
  typeKey: string;
  config: unknown;
  enabled: boolean;
}

export interface CreateWorkflowInput {
  name: string;
  description?: string | null;
  definition: WorkflowDefinition;
  activations: WorkflowActivationInput[];
  enabled: boolean;
}

export interface UpdateWorkflowInput {
  name: string;
  description?: string | null;
  definition: WorkflowDefinition;
  activations: WorkflowActivationInput[];
}

export interface WorkflowRunSummary {
  id: string;
  workflowId: string;
  workflowVersion: number;
  /** Sourced from upstream run_request.source_type. */
  sourceType: WorkflowRunSourceType;
  status: WorkflowRunStatus;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  createdDate: string;
}

export interface WorkflowRunNodeSummary {
  nodeId: string;
  iterationKey: string;
  status: NodeStatus;
  inputs: unknown;
  outputs: unknown;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
}

export interface WorkflowRunDetail {
  id: string;
  workflowId: string;
  workflowVersion: number;
  /** Sourced from run_request.source_type. */
  sourceType: WorkflowRunSourceType;
  /** Normalised payload from the upstream run request — what the engine sees as {{trigger.*}}. */
  normalizedPayload: unknown;
  status: WorkflowRunStatus;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  nodes: WorkflowRunNodeSummary[];
  /** Last successful Terminal's resolved Inputs. */
  outputs?: unknown;
}

export interface NodeManifestDto {
  typeKey: string;
  displayName: string;
  category: string;
  kind: NodeKind;
  description: string | null;
  iconKey: string | null;
  configSchema: unknown;
  inputSchema: unknown;
  outputSchema: unknown;
  /**
   * True for an on-demand trigger (e.g. `trigger.manual`) that starts runs by hand/API rather
   * than by subscribing to an event. `deriveActivations` skips these (no `workflow_activation`
   * row); the runs view uses it to collect inputs before a manual run. Default false/undefined.
   */
  isManual?: boolean;
}

// ─── API client ────────────────────────────────────────────────────────────────

export const workflowsApi = {
  list: () => fetchJson<WorkflowSummary[]>("/api/workflows"),

  get: (workflowId: string) => fetchJson<WorkflowDetail>(`/api/workflows/${workflowId}`),

  create: (input: CreateWorkflowInput) => fetchJson<{ id: string }>("/api/workflows", {
    method: "POST",
    body: JSON.stringify(input),
  }),

  update: (workflowId: string, input: UpdateWorkflowInput) => fetchJson<void>(`/api/workflows/${workflowId}`, {
    method: "PUT",
    body: JSON.stringify(input),
  }),

  delete: (workflowId: string) => fetchJson<void>(`/api/workflows/${workflowId}`, { method: "DELETE" }),

  setEnabled: (workflowId: string, enabled: boolean) => fetchJson<void>(`/api/workflows/${workflowId}/enabled`, {
    method: "POST",
    body: JSON.stringify({ enabled }),
  }),

  runManually: (workflowId: string, payload?: unknown) => fetchJson<{ runId: string }>(`/api/workflows/${workflowId}/run`, {
    method: "POST",
    body: JSON.stringify({ payload: payload ?? null }),
  }),

  listRuns: (workflowId: string, limit = 50) =>
    fetchJson<WorkflowRunSummary[]>(`/api/workflows/${workflowId}/runs?limit=${limit}`),

  getRun: (runId: string) => fetchJson<WorkflowRunDetail>(`/api/workflows/runs/${runId}`),

  /**
   * Replay an existing run. Backend clones release hash + trigger payload + variable
   * snapshot rows onto a fresh run id, then the engine walks the replay path (plain values
   * frozen from snapshot, secrets re-resolved from current variable table).
   */
  replayRun: (runId: string) => fetchJson<{ runId: string }>(`/api/workflows/runs/${runId}/replay`, {
    method: "POST",
  }),

  listNodeManifests: () => fetchJson<NodeManifestDto[]>("/api/workflows/node-manifests"),

  // Engine-injected sys.* variables — sourced from backend's SystemScopeKeys.Descriptors so
  // the SPA doesn't keep a parallel hardcoded list that drifts on rename/addition.
  listSystemVariables: () => fetchJson<SystemVariableDto[]>("/api/workflows/system-variables"),
};

/** Mirrors backend `SystemVariableDto`. Display-only metadata about each sys.* key. */
export interface SystemVariableDto {
  key: string;
  type: string;
  description: string;
}
