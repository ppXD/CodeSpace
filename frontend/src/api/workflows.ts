import { fetchJson } from "./request";
import type { RoomPullRequestResult } from "./sessions";

// ─── Types (mirror backend DTOs) ───────────────────────────────────────────────

export type NodeKind = "Regular" | "Trigger" | "Terminal" | "Loop" | "Try" | "Map";
export type NodeStatus = "Pending" | "Running" | "Success" | "Failure" | "Skipped" | "Suspended";
// Enqueued = dispatched, awaiting worker pickup. Still cancellable; no node activity yet.
// Suspended = paused on a node waiting for a timer / approval / callback; resumes on the signal.
export type WorkflowRunStatus = "Pending" | "Enqueued" | "Running" | "Success" | "Failure" | "Cancelled" | "Suspended";

/** The result of a hard-stop. `cancelled` is true when this call won the flip; false (with the existing terminal `status`) when the run had already finished. `agentRunsCancelled` is how many in-flight agents the kill-wave stopped. */
export interface CancelRunOutcome {
  cancelled: boolean;
  status: WorkflowRunStatus;
  agentRunsCancelled: number;
}

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
  /** Container ownership — set when this node lives inside a flow.loop body. Null/absent at top level. */
  parentId?: string | null;
  /** Static design-time config object. Shape varies per node type. */
  config: unknown;
  /** Dynamic inputs — values can be literals, {{ref}} strings, or { "$ref": "..." } objects. */
  inputs: unknown;
  /** Canvas position (editor-only). When null the editor auto-lays out the node. */
  position?: NodePosition | null;
  /** Explicit container size (editor-only) — set when a loop box was resized by its corner; absent = auto-size to fit the body. */
  width?: number | null;
  height?: number | null;
  /** Optional retry-on-failure policy. Absent = run once (default). */
  retry?: RetryPolicy | null;
}

/**
 * Per-node retry-on-failure policy. Absent on a node = no retry (run once). The engine re-runs
 * the node after a genuine failure up to `maxAttempts` times, waiting `backoffSeconds` between
 * attempts. Suspends + cancellation are never retried. Mirrors the backend RetryPolicy DTO; the
 * engine clamps maxAttempts to [1,10] and backoffSeconds to [0,60].
 */
export interface RetryPolicy {
  maxAttempts: number;
  backoffSeconds: number;
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
  slug: string;
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
  slug: string;
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
  /** Team-scoped sequential number — the run's clean-URL handle (`/runs/{runNumber}`). */
  runNumber: number;
  /** Parent workflow id for an authored run; `null` for a snapshot / task run (it has no parent workflow). */
  workflowId: string | null;
  workflowVersion: number | null;
  /** Parent workflow's display name (`null` for a snapshot / task run) — lets a row show a name without a second lookup. */
  workflowName: string | null;
  /** The run's work-session title (the launching task's human goal), joined from `WorkSession.Title`; `null` for a session-less run. A task row prefers this over the raw source token so it reads as the work. */
  sessionTitle: string | null;
  /** The run's launch-scope repository ids (empty for an authored workflow run). The row resolves display names from the already-loaded team repo set — no per-row name join on the server. */
  repositoryIds: string[];
  /** DB-computed origin class (workflow / task / event / replay / schedule / …). Drives the row's type chip at a friendlier grain than the Workflow/Task binary. */
  runKind: string;
  /** Sourced from upstream run_request.source_type. */
  sourceType: WorkflowRunSourceType;
  status: WorkflowRunStatus;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  createdDate: string;
  /** Whether the run ever parked on a wait. A terminal run that did shows its createdDate→completedAt as a lifespan ("open 5d"), not a runtime clock — the span is dominated by wait time, not work. */
  wasSuspended: boolean;
  /** Lineage key (`rootRunId ?? id`) the index collapses on — a row is always the LATEST attempt of its lineage. */
  rootRunId: string;
  /** How many runs share this lineage root (1 = a never-rerun run). Drives the "N attempts" chip. */
  attemptCount: number;
  /** Whether the run belongs to a work session (`WorkflowRun.SessionId` set). The index opens a session-backed run as the full-page Session room and a session-less run as the raw-detail modal over the list. */
  hasSession: boolean;
  /** The ORIGINAL run's source type (= `sourceType` for a never-rerun run). The row shows the root's identity, so a rerun titles as the original, not "Replay". */
  rootSourceType: WorkflowRunSourceType;
}

/** Mirrors backend `RunAttemptsResponse` — a lineage's attempt ladder (original + every rerun fork), oldest first. */
export interface RunAttemptsResponse {
  rootRunId: string;
  attempts: RunAttempt[];
}

/** Mirrors backend `RunAttemptSummary` — one attempt in a lineage. */
export interface RunAttempt {
  runId: string;
  /** 1-based ordinal within the lineage (1 = the original). */
  attemptNumber: number;
  status: WorkflowRunStatus;
  sourceType: WorkflowRunSourceType;
  /** The node this attempt re-ran from (the map node for a branch rerun); null for the original / a whole-run replay. */
  rerunFromNodeId: string | null;
  createdDate: string;
  /** The newest attempt — selected by default in the detail. */
  isLatest: boolean;
}

/** Mirrors backend `CellAttemptsResponse` — one cell's attempt history (every attempt that ran this node/branch). */
export interface CellAttemptsResponse {
  attempts: CellAttempt[];
}

/** Mirrors backend `CellAttempt` — one attempt's run of a cell. */
export interface CellAttempt {
  /** 1-based ordinal of the owning run within the lineage. */
  attemptNumber: number;
  runId: string;
  /** The agent run this attempt spawned for the cell (null if not an agent node on that attempt). */
  agentRunId: string | null;
  /** This attempt's cell outcome (the node status). */
  status: NodeStatus;
  createdDate: string;
  /** The newest attempt that ran the cell — the merged detail's default. */
  isLatest: boolean;
  /** THIS attempt's own metrics — so switching shows the picked attempt's spend/timing, not the latest's. */
  durationMs?: number | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  costUsd?: number | null;
  filesChanged?: number | null;
  toolCount?: number | null;
  model?: string | null;
}

/**
 * One page of the runs index, in either mode. Keyset (the live feed): `nextCursor` is null on the last page; echo it
 * back as `?cursor=`. Offset (numbered pages, e.g. History): `totalCount` is the total rows matching the filter, so
 * the client can render "page X of Y" and jump to any page. Exactly one of the two is non-null per response.
 */
export interface RunPage {
  items: WorkflowRunSummary[];
  nextCursor: string | null;
  totalCount: number | null;
}

/**
 * The runs cockpit's TRUE scoped counts (the status cards) — each a count over the team's runs narrowed by the bar's
 * scope, not a tally of a loaded page. `suspendedNeedingReview` is the run half of the Needs-attention card (suspended
 * runs no pending decision already covers); the other half is the decision queue. `today` counts runs since the
 * caller's local start-of-day. So nothing-selected is the genuine superset and any filter only narrows.
 */
export interface RunSummary {
  live: number;
  failed: number;
  suspended: number;
  suspendedNeedingReview: number;
  today: number;
}

/**
 * The generic runs-index filter — the client mirror of the backend's RunListFilter. EVERY field is optional and a
 * LIST: values within one field are OR'd, fields are AND'd. ONE shape drives every runs surface — a surface supplies
 * only the dimensions it scopes by (a repo page sets `repositoryIds`, the cockpit's Live card sets `statuses`, the
 * filter bar sets the entity dimensions). Empty / omitted fields are no constraint.
 */
export interface RunListFilterInput {
  workflowIds?: string[];
  statuses?: WorkflowRunStatus[];
  sourceTypes?: string[];
  /** Coarse origin kind — `workflow` / `task` / `event` / `replay` / `schedule` (see the run_kind GENERATED column). */
  runKinds?: string[];
  /** Task projection/coordination mode — `single-agent` / `supervisor`. */
  projectionKinds?: string[];
  repositoryIds?: string[];
  projectIds?: string[];
  /** Users who launched the run. */
  actorIds?: string[];
  /** Agent personas the run used (matches an agent spawned on ANY turn). */
  agentDefinitionIds?: string[];
  hasPendingDecision?: boolean;
  needsAttention?: boolean;
  /** Inclusive lower / exclusive upper bound on createdDate (ISO 8601). */
  since?: string;
  until?: string;
}

/**
 * Serialize a runs filter (+ paging) into the query string the runs index binds. Each list field emits one repeated
 * param per value (`repositoryIds=a&repositoryIds=b`); booleans/dates emit one param; empty / undefined fields are
 * omitted so the URL — and the React Query cache key derived from it — stays canonical (equivalent filters serialize
 * identically). Field order is fixed, so a given filter always produces the same string.
 */
export function buildRunListParams(filter: RunListFilterInput | undefined, limit: number, cursor?: string, page?: number): string {
  const p = new URLSearchParams();
  p.set("limit", String(limit));
  if (cursor) p.set("cursor", cursor);
  if (page !== undefined) p.set("page", String(page));   // offset (numbered) mode; the server ignores `cursor` when set

  if (filter) {
    const lists: [string, readonly string[] | undefined][] = [
      ["workflowIds", filter.workflowIds],
      ["statuses", filter.statuses],
      ["sourceTypes", filter.sourceTypes],
      ["runKinds", filter.runKinds],
      ["projectionKinds", filter.projectionKinds],
      ["repositoryIds", filter.repositoryIds],
      ["projectIds", filter.projectIds],
      ["actorIds", filter.actorIds],
      ["agentDefinitionIds", filter.agentDefinitionIds],
    ];
    for (const [key, values] of lists) for (const v of values ?? []) p.append(key, v);

    if (filter.hasPendingDecision !== undefined) p.set("hasPendingDecision", String(filter.hasPendingDecision));
    if (filter.needsAttention !== undefined) p.set("needsAttention", String(filter.needsAttention));
    if (filter.since) p.set("since", filter.since);
    if (filter.until) p.set("until", filter.until);
  }

  return p.toString();
}

export interface WorkflowRunNodeSummary {
  nodeId: string;
  iterationKey: string;
  /**
   * The typeKey of the container that owns this row's innermost iteration — "flow.map" for a map
   * element-branch, "flow.loop" for a loop body, "flow.try" for a try body; `null`/absent for a
   * top-level (non-iterated) row. The engine builds a loop body key (`<loopId>#<i>`) and a map branch
   * key (`<mapId>#<i>`) with the SAME shape, so `iterationKey` alone can't distinguish them — this is
   * what lets the run-detail view badge / roll up ONLY map fan-outs and keep loops as plain rows.
   */
  containerKind?: string | null;
  status: NodeStatus;
  inputs: unknown;
  outputs: unknown;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  /**
   * For a `flow.subworkflow` node — the id of the child run this step spawned. Lets the run-detail
   * view embed / link the child run inline for this step (in any state). `null`/absent otherwise.
   */
  childRunId?: string | null;
  /**
   * For an `agent.run` node — the id of the agent run this step spawned. Lets the run-detail view
   * embed the run's live status + event timeline inline for this step. `null`/absent otherwise.
   */
  agentRunId?: string | null;
  /**
   * Whether a from-node rerun (`POST /runs/{id}/rerun-from-node`) would be ACCEPTED with this node as the
   * target — computed server-side by the SAME gate the rerun endpoint enforces. The UI offers "Rerun from
   * here" ONLY when true, instead of surfacing a button that 422s on click. Always `false`/absent for an
   * iterated (container-body) row.
   */
  rerunnableFromHere?: boolean;
}

/** The outstanding wait a Suspended run is parked on — drives the resume affordance. */
export interface WorkflowRunWaitInfo {
  nodeId: string;
  /** "Timer" | "Approval" | "Callback" | "Subworkflow". */
  kind: string;
  /** Correlation token — for a Callback wait, the secret the callback URL is built from. */
  token: string;
  /** When the scheduled resume fires (Timer only). */
  wakeAt?: string | null;
  /** The node's suspend payload (e.g. an approval `prompt`). */
  payload?: unknown;
}

export interface WorkflowRunDetail {
  id: string;
  /** Team-scoped sequential number — the run's clean-URL handle (`/runs/{runNumber}`). */
  runNumber: number;
  workflowId: string;
  workflowVersion: number;
  /** Sourced from run_request.source_type. */
  sourceType: WorkflowRunSourceType;
  /** The run this one forked from — set for a replay / rerun. The header threads the lineage off it. */
  parentRunId?: string | null;
  /** Normalised payload from the upstream run request — what the engine sees as {{trigger.*}}. */
  normalizedPayload: unknown;
  status: WorkflowRunStatus;
  error: string | null;
  startedAt: string | null;
  completedAt: string | null;
  /** Run-creation time (immutable). Wall-clock duration = createdDate → completedAt (startedAt is reset per resume). */
  createdDate: string;
  nodes: WorkflowRunNodeSummary[];
  /**
   * The EXACT graph this run executed — the version-pinned snapshot, NOT the workflow's current
   * definition — so the run canvas stays faithful to how the run actually ran after later edits.
   * `null`/absent only when the snapshot couldn't be loaded.
   */
  definition?: WorkflowDefinition | null;
  /** Last successful Terminal's resolved Inputs. */
  outputs?: unknown;
  /** Set when the run is Suspended — tells the UI why it's paused + what affordance to show. */
  pendingWait?: WorkflowRunWaitInfo | null;
}

/**
 * An author-facing starter template a node declares in its manifest — a ready-to-use (config, inputs)
 * pair the editor applies on "start from a template". A friendly surface over the generic schemas;
 * the engine never reads it. Mirrors backend NodePresetDto.
 */
export interface NodePreset {
  id: string;
  label: string;
  description?: string | null;
  config: Record<string, unknown>;
  inputs: Record<string, unknown>;
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
  /** True when the node has external side effects (opens a PR, comments, merges, runs a command). Badged "Writes". */
  isSideEffecting?: boolean;
  /** True when the node can SUSPEND the run (agent run, human decision, sleep, sub-workflow). Badged "Waits". */
  canSuspend?: boolean;
  /** True when the node always parks on a human-approval gate before its effect. Badged "Approval". */
  alwaysRequiresApproval?: boolean;
  /** Named output handles (routing branches, e.g. logic.if's true/false). One labelled source handle each; absent ⇒ a single default output. */
  outputs?: NodeOutputHandle[];
  /** Starter templates the editor offers as "start from a template". Absent/empty ⇒ none. */
  presets?: NodePreset[];
}

/** Mirrors backend `NodeOutputHandleDto` — one named routing branch; `name` matches the engine's route handle. */
export interface NodeOutputHandle {
  name: string;
  displayName?: string | null;
  description?: string | null;
}

// ─── Run phases (the run-outline projection — GET /api/workflows/runs/{id}/phases) ───────────────

/** The ONLY closed axis of a phase — the render vocabulary. Everything else (kind, agent status) is an open string. */
export type PhaseStatus = "Pending" | "Active" | "Waiting" | "Succeeded" | "Failed" | "Skipped";

/** Mirrors backend `PhaseAgentRef` — one agent run a phase fanned out to. `status` is the open AgentRunStatus name. */
export interface PhaseAgentRef {
  agentRunId: string;
  nodeId?: string | null;
  iterationKey?: string | null;
  status: string;
  label?: string | null;
  /** The model-authored semantic ROLE this agent runs in (e.g. "backend implementer"), off the spawn's per-agent dispatch. null/absent for a homogeneous spawn or a non-supervisor agent. */
  role?: string | null;
  /** The TITLE of the planned subtask this agent was assigned (the model's decomposition). null/absent when not a supervisor spawn. */
  assignedSubtask?: string | null;
  /** The model the agent ran on, or null/absent when unpinned/unknown. Populated for supervisor-spawned agents. */
  model?: string | null;
  /** Input (prompt) tokens the agent consumed, or null/absent when unknown. Supervisor-spawned agents only. */
  inputTokens?: number | null;
  /** Output (completion) tokens the agent produced, or null/absent when unknown. */
  outputTokens?: number | null;
  /** Run duration in ms — final once terminal, else live elapsed at the last poll; null/absent for a non-supervisor agent or before it starts. The Time column. */
  durationMs?: number | null;
  /** Side-effecting tool calls the agent made (ledger rows minus decision.request); `0` is a real "made none", null/absent when the agent row is missing. The Tools column. */
  toolCount?: number | null;
  /** Realized spend in USD — model price × tokens, computed server-side. null when the model is unpriced (fail-open) or before tokens land. */
  costUsd?: number | null;
  /** Git-truth count of files the agent changed (off the result's changedFiles, not a live event tally). null before the result lands; `0` is a real "touched none". */
  filesChanged?: number | null;
  /** The agent's OWN changed-file paths (the Files tab). Optional — populated by the Session Room card mapping; a bare phase ref carries only the count above. */
  changedFiles?: string[] | null;
}

/** Mirrors backend `PhaseMetrics` — the small roll-up a phase row shows. */
export interface PhaseMetrics {
  agentCount: number;
  succeededCount: number;
  failedCount: number;
  extra?: Record<string, unknown>;
}

/**
 * Mirrors backend `RunPhase` — one row of a run's outline (a node, a map fan-out, an agent step, a supervisor
 * decision, a model-authored phase). `kind` is an OPEN string the UI never switches on; only `status` is closed.
 * This is the run-neutral projection: the SAME shape backs a single-agent run, a workflow, and a Deep supervisor.
 */
export interface RunPhase {
  id: string;
  label: string;
  kind: string;
  status: PhaseStatus;
  order: number;
  agents: PhaseAgentRef[];
  metrics: PhaseMetrics;
  summary?: string | null;
  sourceKey: string;
  startedAt?: string | null;
  completedAt?: string | null;
}

/** Mirrors backend `TaskRunPhasesResponse` — the run's overall status + the merged, order-sorted phase tree. */
export interface RunPhasesResponse {
  runId: string;
  runStatus: WorkflowRunStatus;
  phases: RunPhase[];
}

// ─── Run narrative timeline (the merged event story — GET /api/workflows/runs/{id}/timeline) ──────

/** The closed render-tone axis of a timeline event. `kind` and everything else is an open string. */
export type TimelineSeverity = "Info" | "Success" | "Warning" | "Error";

/** The closed narrative-prominence axis — a `Milestone` shows in the story; a `Detail` folds into a "N steps" disclosure. */
export type TimelineLevel = "Milestone" | "Detail";

/**
 * Mirrors backend `RunTimelineEvent` — one event on the run's narrative timeline (a run/node lifecycle step, an
 * agent's file edit, …). FLAT + source-agnostic: the UI never switches on `kind` (an OPEN string), only on the two
 * closed axes `severity` (tone) + `level` (prominence). Events arrive merged + chronologically sorted; `sourceKey` is the provenance.
 */
export interface RunTimelineEvent {
  id: string;
  kind: string;
  title: string;
  summary?: string | null;
  severity: TimelineSeverity;
  /** Narrative prominence; absent (forward-tolerance) reads as a milestone — never silently folded. */
  level?: TimelineLevel | null;
  occurredAt: string;
  nodeId?: string | null;
  agentRunId?: string | null;
  sourceKey: string;
}

/** Mirrors backend `RunTimelineResponse` — the run's status + the merged, chronologically-sorted narrative events. */
export interface RunTimelineResponse {
  runId: string;
  runStatus: WorkflowRunStatus;
  events: RunTimelineEvent[];
}

/**
 * Mirrors backend `RunRecordView` — one raw row of the run's append-only event ledger (the Trace audit). `recordType`
 * is an OPEN string (e.g. "run.started", "node.completed", "log") — render unknown types as-is. `payloadJson` is the
 * raw, secret-redacted, jsonb-normalized payload — JSON.parse it for display.
 */
export interface RunRecordView {
  sequence: number;
  recordType: string;
  nodeId?: string | null;
  iterationKey: string;
  occurredAt: string;
  payloadJson: string;
  correlationId?: string | null;
  parentRecordId?: string | null;
}

/** Mirrors backend `RunRecordsResponse` — the run's status + every raw ledger record, in Sequence order (the Trace tab). */
export interface RunRecordsResponse {
  runId: string;
  runStatus: WorkflowRunStatus;
  records: RunRecordView[];
}

// ─── Decisions (the cross-grain "Needs decision" queue — GET /api/workflows/decisions) ───────────

/** The shape of the ask — an OPEN string (forward-compatible); the UI maps a known set to affordances and free-texts the rest. */
export type DecisionType = "confirm" | "choose_one" | "choose_many" | "free_text" | "approve_action";

/** Mirrors backend `DecisionOption` — one selectable choice; `isSideEffecting` marks an irreversible outcome. */
export interface DecisionOption {
  id: string;
  label: string;
  isSideEffecting?: boolean;
}

/**
 * Mirrors backend `PendingDecision` — one PENDING item in the unified queue, projected over BOTH park grains
 * (an `agent.run` mid-run `decision.request` AND a `flow.decision` node wait). `rootTraceId` is the run-tree
 * key the Run Room filters on; `grain`/`decisionType`/`riskLevel`/`policy` are open strings.
 */
export interface PendingDecision {
  id: string;
  grain: string;
  rootTraceId: string;
  workflowRunId?: string | null;
  agentRunId?: string | null;
  nodeId?: string | null;
  decisionType: DecisionType;
  question: string;
  options: DecisionOption[];
  recommendedOption?: string | null;
  blockingReason?: string | null;
  contextSummary?: string | null;
  riskLevel: string;
  policy: string;
  createdAt: string;
  deadlineAt?: string | null;
  answerMessageId?: string | null;
}

/** Body for POST /api/workflows/decisions/{id}/answer — chosen option id(s) and/or a free-text answer. */
export interface AnswerDecisionInput {
  selectedOptions?: string[];
  freeText?: string | null;
}

/** Mirrors backend `DecisionAnswerOutcome`. */
export type DecisionAnswerOutcome = "Answered" | "AlreadyResolved" | "NotFound" | "Invalid" | "RequiresHuman";

/** Mirrors backend `AnswerDecisionResult`. */
export interface AnswerDecisionResult {
  outcome: DecisionAnswerOutcome;
  message?: string | null;
}

// ─── API client ────────────────────────────────────────────────────────────────

export const workflowsApi = {
  list: () => fetchJson<WorkflowSummary[]>("/api/workflows"),

  /** Resolve one workflow by ref — its GUID (legacy link) or team-unique slug (clean URL). */
  get: (ref: string) => fetchJson<WorkflowDetail>(`/api/workflows/${encodeURIComponent(ref)}`),

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

  /** The team's runs index — every top-level run the team owns (any source), newest first; keyset-paginated + filterable. */
  listTeamRuns: (filter?: RunListFilterInput, limit = 50, cursor?: string) =>
    fetchJson<RunPage>(`/api/workflows/runs?${buildRunListParams(filter, limit, cursor)}`),

  /** The same index, OFFSET-paginated for numbered pages (1-based `page`): the response carries `totalCount` for "page X of Y". */
  listTeamRunsPage: (filter: RunListFilterInput | undefined, page: number, pageSize: number) =>
    fetchJson<RunPage>(`/api/workflows/runs?${buildRunListParams(filter, pageSize, undefined, page)}`),

  /** The cockpit's true scoped counts for the status cards. `todayStartIso` is the caller's local start-of-day for the today count. */
  summarizeTeamRuns: (filter: RunListFilterInput | undefined, todayStartIso: string) =>
    fetchJson<RunSummary>(`/api/workflows/runs/summary?${buildRunListParams(filter, 1)}&today=${encodeURIComponent(todayStartIso)}`),

  /** Resolve one run by ref — its team-scoped run number (clean URL) or GUID (legacy link). */
  getRun: (ref: string) => fetchJson<WorkflowRunDetail>(`/api/workflows/runs/${encodeURIComponent(ref)}`),

  /** The lineage's attempt ladder (original + every rerun fork) — drives the run-detail attempt switcher. */
  getRunAttempts: (runId: string) => fetchJson<RunAttemptsResponse>(`/api/workflows/runs/${runId}/attempts`),

  /** One cell's attempt history (every attempt that ran this node/branch) — drives the per-cell rerun history in the terminal. */
  getCellAttempts: (runId: string, nodeId: string, iterationKey: string) =>
    fetchJson<CellAttemptsResponse>(`/api/workflows/runs/${runId}/cells/attempts?nodeId=${encodeURIComponent(nodeId)}&iterationKey=${encodeURIComponent(iterationKey)}`),

  /** The run's outline — the merged, order-sorted phase tree projected over the durable substrate (run-neutral). */
  getRunPhases: (runId: string) => fetchJson<RunPhasesResponse>(`/api/workflows/runs/${runId}/phases`),

  getRunTimeline: (runId: string) => fetchJson<RunTimelineResponse>(`/api/workflows/runs/${runId}/timeline`),

  /** The run's RAW event ledger — every record unfiltered, in Sequence order (the Trace audit). */
  getRunRecords: (runId: string) => fetchJson<RunRecordsResponse>(`/api/workflows/runs/${runId}/records`),

  /** The team's cross-grain pending decisions, soonest-deadline first. The Run Room filters by `rootTraceId`. */
  listPendingDecisions: () => fetchJson<PendingDecision[]>("/api/workflows/decisions"),

  /** Answer a pending decision (either grain) — the route id is the authority; the body is the answer. */
  answerDecision: (decisionId: string, body: AnswerDecisionInput) =>
    fetchJson<AnswerDecisionResult>(`/api/workflows/decisions/${decisionId}/answer`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /**
   * Replay an existing run. Backend clones release hash + trigger payload + variable
   * snapshot rows onto a fresh run id, then the engine walks the replay path (plain values
   * frozen from snapshot, secrets re-resolved from current variable table).
   */
  replayRun: (runId: string) => fetchJson<{ runId: string }>(`/api/workflows/runs/${runId}/replay`, {
    method: "POST",
  }),

  /**
   * The Room's "Open PR" action (PR-6): opens (or, on a repeat call, reuses) a pull/merge request for a terminal
   * run's published branch(es). One entry per repository the run published to.
   */
  openPullRequest: (runId: string) => fetchJson<RoomPullRequestResult>(`/api/workflows/runs/${runId}/open-pull-request`, {
    method: "POST",
  }),

  /**
   * Re-run ONE branch (one fanned-out item) of a top-level flow.map. Forks a run that reuses the sibling items and
   * re-runs this one + the map's downstream. `operationId` is a client-minted idempotency token (one per click →
   * a double-submit / retry returns the SAME fork). Returns the new run id.
   */
  rerunMapBranch: (runId: string, body: { mapNodeId: string; branchIndex: number; operationId: string }) =>
    fetchJson<{ runId: string }>(`/api/workflows/runs/${runId}/rerun-map-branch`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /** Re-run a SET of a top-level flow.map's branches ("Rerun all failed items") in ONE fork. Same idempotency token contract. */
  rerunMapBranches: (runId: string, body: { mapNodeId: string; branchIndices: number[]; operationId: string }) =>
    fetchJson<{ runId: string }>(`/api/workflows/runs/${runId}/rerun-map-branches`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /** Re-run FROM a node ("Rerun from here") — forks a run that reuses everything upstream and re-runs this node + its downstream. */
  rerunFromNode: (runId: string, body: { fromNodeId: string }) =>
    fetchJson<{ runId: string }>(`/api/workflows/runs/${runId}/rerun-from-node`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /** Resolve a pending approval on a Suspended run + resume it. Returns whether it resumed. */
  resumeRun: (runId: string, body: { approved: boolean; comment?: string }) =>
    fetchJson<{ resumed: boolean }>(`/api/workflows/runs/${runId}/resume`, {
      method: "POST",
      body: JSON.stringify(body),
    }),

  /** Continue a STRANDED Suspended run (no pending wait) on demand — drives the same re-dispatch the reconciler does. Returns whether this call drove it. */
  continueRun: (runId: string) =>
    fetchJson<{ continued: boolean }>(`/api/workflows/runs/${runId}/continue`, { method: "POST" }),

  /**
   * Cancel (hard-stop) a still-live run — wins the non-terminal → Cancelled flip and kills the run's in-flight
   * agents. Idempotent: an already-terminal run returns `cancelled: false` carrying its existing status.
   */
  cancelRun: (runId: string) => fetchJson<CancelRunOutcome>(`/api/workflows/runs/${runId}/cancel`, {
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
