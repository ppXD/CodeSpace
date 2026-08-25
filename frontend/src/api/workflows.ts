import { ApiError, fetchJson, fetchResponse } from "./request";
import type { RoomFileIdentity, RoomPullRequestResult } from "./sessions";

// ─── Types (mirror backend DTOs) ───────────────────────────────────────────────

export type NodeKind = "Regular" | "Trigger" | "Terminal" | "Loop" | "Try" | "Map";
export type NodeStatus = "Pending" | "Running" | "Success" | "Failure" | "Skipped" | "Suspended";
// Enqueued = dispatched, awaiting worker pickup. Still cancellable; no node activity yet.
// Suspended = paused on a node waiting for a timer / approval / callback; resumes on the signal.
export type WorkflowRunStatus = "Pending" | "Enqueued" | "Running" | "Success" | "Failure" | "Cancelled" | "Suspended";

/** Bounded canonical route identity; deliberately excludes graph, outputs, cells, waits and artifacts. */
export interface WorkflowRunIdentity {
  id: string;
  runNumber: number;
  status: WorkflowRunStatus;
}

export class InvalidWorkflowRunIdentityError extends Error {
  constructor() {
    super("Invalid Workflow Run identity response.");
    this.name = "InvalidWorkflowRunIdentityError";
  }
}

export class InvalidWorkflowRunPendingWaitObservationError extends Error {
  constructor() {
    super("Invalid Workflow Run pending-wait observation response.");
    this.name = "InvalidWorkflowRunPendingWaitObservationError";
  }
}

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
  /** A1: how the WORK ended, when the status alone would mislead — "Succeeded" | "GaveUp" | "Forced" |
   *  "NeedsClarification" | "AcceptanceFailed". `status` says the graph finished; a give-up, a bound-forced stop,
   *  an abstention and a failed objective check all finish as Success. ABSENT for a non-supervisor run and for
   *  every run that terminalized before the column existed — absence means "the status word is the whole truth",
   *  never a verdict, so every reader falls back rather than treating it as a degradation. */
  outcome?: string | null;
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

export type WorkflowRunPendingWaitPromptState = "Missing" | "Exact" | "Truncated" | "Invalid";

/** Bounded action descriptor; the raw wait payload and run graph never cross this read seam. */
export interface WorkflowRunPendingWaitObservation {
  runId: string;
  wait: WorkflowRunPendingWait | null;
}

export interface WorkflowRunPendingWait {
  id: string;
  nodeId: string;
  kind: string;
  token: string;
  wakeAt: string | null;
  promptState: WorkflowRunPendingWaitPromptState;
  promptPrefix: string | null;
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
  /** Exact repository + producing-attempt identities behind `changedFiles`; absent on legacy/bare phase refs. */
  changedFileIdentities?: RoomFileIdentity[] | null;
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

/** Body-free metadata returned by the bounded page. Payload bytes are fetched only for one exact expanded record. */
export interface RunRecordPageItem {
  recordId: string;
  sequence: number;
  recordType: string;
  nodeId: string | null;
  iterationKey: string;
  occurredAt: string;
  payloadState: "Deferred";
  payloadContentType: "application/json";
  correlationId: string | null;
  parentRecordId: string | null;
}

/** Closed keyset direction returned by the bounded raw-ledger reader. */
export type RunRecordPageMode = "Tail" | "Older" | "Newer";

/** One bounded page from GET /api/workflows/runs/{id}/records/page. */
export interface RunRecordPageResponse {
  runId: string;
  runStatus: WorkflowRunStatus;
  mode: RunRecordPageMode;
  records: RunRecordPageItem[];
  nextBeforeSequence: number | null;
  nextAfterSequence: number | null;
}

/** Exactly one keyset direction: neither cursor is Tail, before is Older, after is Newer. */
export interface RunRecordPageRequest {
  beforeSequence?: number;
  afterSequence?: number;
  limit: number;
}

/** A non-retryable wire-contract violation; transport/backend faults remain ordinary retryable errors. */
export class InvalidWorkflowRunRecordPageError extends Error {
  constructor() {
    super("Invalid Workflow Run record page response.");
    this.name = "InvalidWorkflowRunRecordPageError";
  }
}

export type RunRecordPayloadReadAvailability = "Missing" | "InvalidRange" | "BackendUnavailable" | "AccessDenied" | "InvalidResponse";

export interface RunRecordPayloadRangeAvailable {
  availability: "Available";
  bytes: Uint8Array;
  runId: string;
  recordId: string;
  sequence: number;
  offsetBytes: number;
  nextOffsetBytes: number | null;
  totalBytes: number;
  contentType: "application/json";
}

export interface RunRecordPayloadRangeProblem {
  availability: RunRecordPayloadReadAvailability;
  code: string;
  isRetryable: boolean;
}

export type RunRecordPayloadRangeResult = RunRecordPayloadRangeAvailable | RunRecordPayloadRangeProblem;

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

export type WorkflowRunModelCallStatus = "Completed" | "Failed";
export type WorkflowRunModelCallProjectionState = "Projected" | "LegacyFallback";
export type WorkflowRunCaptureCompleteness = "Exact" | "RedactedExact" | "Partial" | "Unavailable" | "Corrupt" | "LegacyUnknown";
export type WorkflowRunDataCompletenessScope = "RecordedFacetsOnly";

export type WorkflowRunToolCallEffectClass = "ReadOnly" | "SideEffecting" | "Unknown" | "LegacyUnknown" | "Corrupt";
export type WorkflowRunToolCallObservationState = "Pending" | "Running" | "Completed" | "Abandoned" | "LegacyUnknown" | "Corrupt";
export type WorkflowRunToolCallAttemptStatus = "Pending" | "Running" | "Succeeded" | "Failed" | "Denied" | "Cancelled" | "TimedOut" | "Indeterminate" | "LegacyUnknown" | "Corrupt";
export type WorkflowRunToolCallErrorCode = "LedgerFailedOutcomeUnknown" | "GovernanceDenied" | "ApprovalExpired" | "LegacyUnknown" | "Corrupt";

/** Metadata-only observation. CallOrdinal is per Agent Run, never the Workflow Run page order. */
export interface WorkflowRunToolCallMetadata {
  toolCallId: string;
  runId: string;
  toolAdapterKind: string;
  toolName: string;
  effectClass: WorkflowRunToolCallEffectClass;
  state: WorkflowRunToolCallObservationState;
  callOrdinal: number;
  sourceKind: string | null;
  sourceCorrelationId: string | null;
  captureSource: string;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  createdAt: string;
  lastModifiedAt: string;
  terminalAt: string | null;
  errorCode: WorkflowRunToolCallErrorCode | null;
}

export interface WorkflowRunToolCallAttemptMetadata {
  attemptOrdinal: number;
  status: WorkflowRunToolCallAttemptStatus;
  captureSource: string;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  /** Source admission lower-bound, not an observed provider wire start. */
  startedAt: string;
  completedAt: string | null;
  createdAt: string;
  lastModifiedAt: string;
  errorCode: WorkflowRunToolCallErrorCode | null;
}

export interface WorkflowRunToolCallPage {
  runId: string;
  requestCursor: string | null;
  limit: number;
  items: WorkflowRunToolCallMetadata[];
  nextCursor: string | null;
}

export interface WorkflowRunToolCallDetail {
  call: WorkflowRunToolCallMetadata;
  attempts: WorkflowRunToolCallAttemptMetadata[];
  attemptsTruncated: boolean;
}

export interface WorkflowRunToolCallPageRequest {
  cursor?: string;
  limit: number;
}

/** A non-retryable request/wire-contract violation; transport faults remain retryable errors. */
export class InvalidWorkflowRunToolCallResponseError extends Error {
  constructor() {
    super("Invalid Workflow Run tool-call response.");
    this.name = "InvalidWorkflowRunToolCallResponseError";
  }
}

export interface WorkflowRunDataFacetCompleteness {
  /** Open registered facet identity; future producer facets render without a frontend release. */
  facet: string;
  expectedRecordCount: number | null;
  presentRecordCount: number;
  knownMissingCount: number;
  verdict: WorkflowRunCaptureCompleteness;
  isStrictlyReadable: boolean;
  revision: number;
  schemaVersion: number;
  lastModifiedAt: string;
}

/** Observation-only producer statements with a terminal-only fold over the explicitly registered producer coverage. */
export interface WorkflowRunDataCompletenessView {
  runId: string;
  scope: WorkflowRunDataCompletenessScope;
  facets: WorkflowRunDataFacetCompleteness[];
  hasStatements: boolean;
  isTerminal: boolean;
  requiredFacets: string[];
  missingFacetStatements: string[];
  runWideVerdict: WorkflowRunCaptureCompleteness | null;
  truncated: boolean;
}
export type WorkflowRunModelCallPart = "Result" | "SystemPrompt" | "UserPrompt" | "Usage" | "Trace" | "Error";
export type WorkflowRunModelCallPartSource = "NotRecorded" | "Inline" | "Artifact" | "Synthesized";
export type WorkflowRunModelCallPartAvailability = "Available" | "NotRecorded" | "MetadataMissing" | "PhysicalObjectMissing" | "IntegrityFailure" | "BackendUnavailable" | "AccessDenied" | "InvalidOffset" | "Redacted" | "CapturePartial" | "CaptureUnavailable" | "CaptureCorrupt" | "LegacyUnknown" | "InvalidBodyReference";

export interface WorkflowRunModelCallPartDescriptor {
  part: WorkflowRunModelCallPart;
  source: WorkflowRunModelCallPartSource;
  sizeBytes?: number | null;
  contentType?: string | null;
  artifactId?: string | null;
}

/** Metadata-only Workflow Run model-call projection; content is fetched one selected, bounded part at a time. */
export interface WorkflowRunModelCallMetadata {
  runId: string;
  sequence: number;
  workflowRunModelCallId?: string | null;
  projectionState: WorkflowRunModelCallProjectionState;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  correlationId?: string | null;
  status: WorkflowRunModelCallStatus;
  parts: WorkflowRunModelCallPartDescriptor[];
}

export interface WorkflowRunModelCallPartPage {
  part: WorkflowRunModelCallPart;
  availability: WorkflowRunModelCallPartAvailability;
  text?: string | null;
  offsetBytes: number;
  returnedBytes: number;
  totalBytes?: number | null;
  nextOffsetBytes?: number | null;
  contentType?: string | null;
  artifactId?: string | null;
  integrityVerified: boolean;
  message?: string | null;
}

export type WorkflowRunModelCallBody = "LogicalRequest" | "AttemptRequest" | "AttemptResponse" | "AttemptError";
export type WorkflowRunModelCallBodyReferenceState = "Referenced" | "NotRecorded" | "Redacted" | "Partial" | "Unavailable" | "Corrupt" | "LegacyUnknown";
export type WorkflowRunModelCallBodyCaptureHealth = "Pending" | "Materializing" | "Retry" | "Available" | "Failed" | "Abandoned";
export type WorkflowRunModelCallSourceEvidence = "Native" | "TerminalOnly" | "StartedAndTerminal" | "LateStartAttached";

export interface WorkflowRunModelCallListItem {
  workflowRunModelCallId: string;
  runId: string;
  callOrdinal: number;
  nodeId: string | null;
  iterationKey: string;
  executionAttemptId: string | null;
  purpose: string;
  requestedProvider: string | null;
  requestedModel: string | null;
  captureSource: string;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  createdAt: string;
}

export interface WorkflowRunModelCallPage {
  runId: string;
  requestCursor: string | null;
  limit: number;
  items: WorkflowRunModelCallListItem[];
  nextCursor: string | null;
}

export class InvalidWorkflowRunModelCallPageError extends Error {
  constructor() {
    super("Invalid Workflow Run model-call page.");
    this.name = "InvalidWorkflowRunModelCallPageError";
  }
}

export interface WorkflowRunModelCallBodyDescriptor {
  body: WorkflowRunModelCallBody;
  attemptId?: string | null;
  artifactId?: string | null;
  referenceState: WorkflowRunModelCallBodyReferenceState;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  captureHealth?: WorkflowRunModelCallBodyCaptureHealth | null;
  materializationFormat?: string | null;
}

export interface WorkflowRunModelCallUsageMetadata {
  inputTokens?: number | null;
  outputTokens?: number | null;
  cacheReadTokens?: number | null;
  cacheWriteTokens?: number | null;
  reasoningTokens?: number | null;
}

export interface WorkflowRunModelCallAttemptMetadata {
  attemptId: string;
  attemptOrdinal: number;
  effectiveProvider?: string | null;
  effectiveModel?: string | null;
  effectiveModelRowId?: string | null;
  transportKind?: string | null;
  endpointFingerprint?: string | null;
  providerRequestId?: string | null;
  status: string;
  errorCode?: string | null;
  finishReason?: string | null;
  httpStatusCode?: number | null;
  captureSource: string;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  sourceEvidence: WorkflowRunModelCallSourceEvidence;
  sourceStartedRecordId?: string | null;
  sourceTerminalRecordId?: string | null;
  sourceEvidenceRevision: number;
  /** Figures this producer explicitly could not observe; null without a member here is only unstated. */
  unavailableFigures: string[];
  usage: WorkflowRunModelCallUsageMetadata;
  costAmount?: number | null;
  costCurrency?: string | null;
  pricingVersion?: string | null;
  startedAt: string;
  firstTokenAt?: string | null;
  completedAt?: string | null;
  schemaVersion: number;
  bodies: WorkflowRunModelCallBodyDescriptor[];
}

/** Byte-free logical call plus its ordered physical provider attempts. */
export interface WorkflowRunModelCallDetailMetadata {
  workflowRunModelCallId: string;
  runId: string;
  callOrdinal: number;
  nodeId?: string | null;
  iterationKey: string;
  workPlanId?: string | null;
  planVersion?: number | null;
  workUnitId?: string | null;
  workUnitContractHash?: string | null;
  executionAttemptId?: string | null;
  executionAttemptOrdinal?: number | null;
  executionGeneration?: number | null;
  purpose: string;
  requestedProvider?: string | null;
  requestedModel?: string | null;
  requestedModelRowId?: string | null;
  selectionPolicy?: string | null;
  sourceKind?: string | null;
  sourceCorrelationId?: string | null;
  captureSource: string;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  schemaVersion: number;
  createdAt: string;
  bodies: WorkflowRunModelCallBodyDescriptor[];
  attempts: WorkflowRunModelCallAttemptMetadata[];
}

export interface WorkflowRunModelCallBodyPage {
  body: WorkflowRunModelCallBody;
  attemptId?: string | null;
  captureCompleteness: WorkflowRunCaptureCompleteness;
  availability: WorkflowRunModelCallPartAvailability;
  text?: string | null;
  offsetBytes: number;
  returnedBytes: number;
  totalBytes?: number | null;
  nextOffsetBytes?: number | null;
  contentType?: string | null;
  artifactId?: string | null;
  integrityVerified: boolean;
  message?: string | null;
}

export interface WorkflowRunModelCallBodyRead {
  body: WorkflowRunModelCallBody;
  attemptId?: string | null;
  offsetBytes: number;
  limitBytes: number;
}

const WORKFLOW_RUN_CAPTURE_COMPLETENESS = new Set<WorkflowRunCaptureCompleteness>(["Exact", "RedactedExact", "Partial", "Unavailable", "Corrupt", "LegacyUnknown"]);
const WORKFLOW_RUN_STATUSES = new Set<WorkflowRunStatus>(["Pending", "Enqueued", "Running", "Success", "Failure", "Cancelled", "Suspended"]);
const WORKFLOW_RUN_PENDING_WAIT_PROMPT_STATES = new Set<WorkflowRunPendingWaitPromptState>(["Missing", "Exact", "Truncated", "Invalid"]);
const WORKFLOW_RUN_PENDING_WAIT_PROMPT_MAX = 2048;
const RUN_RECORD_PAGE_MODES = new Set<RunRecordPageMode>(["Tail", "Older", "Newer"]);
const WORKFLOW_RUN_TOOL_CALL_EFFECTS = new Set<WorkflowRunToolCallEffectClass>(["ReadOnly", "SideEffecting", "Unknown", "LegacyUnknown", "Corrupt"]);
const WORKFLOW_RUN_TOOL_CALL_STATES = new Set<WorkflowRunToolCallObservationState>(["Pending", "Running", "Completed", "Abandoned", "LegacyUnknown", "Corrupt"]);
const WORKFLOW_RUN_TOOL_CALL_ATTEMPT_STATUSES = new Set<WorkflowRunToolCallAttemptStatus>(["Pending", "Running", "Succeeded", "Failed", "Denied", "Cancelled", "TimedOut", "Indeterminate", "LegacyUnknown", "Corrupt"]);
const WORKFLOW_RUN_TOOL_CALL_ERROR_CODES = new Set<WorkflowRunToolCallErrorCode>(["LedgerFailedOutcomeUnknown", "GovernanceDenied", "ApprovalExpired", "LegacyUnknown", "Corrupt"]);
const WORKFLOW_RUN_TOOL_CALL_CURSOR_MAX = 96;
const WORKFLOW_RUN_TOOL_CALL_PAGE_MAX = 200;
const WORKFLOW_RUN_TOOL_CALL_ATTEMPT_MAX = 100;
const WORKFLOW_RUN_MODEL_CALL_CURSOR_MAX = 128;
const WORKFLOW_RUN_MODEL_CALL_PAGE_MAX = 200;

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function decodeWorkflowRunIdentity(value: unknown): WorkflowRunIdentity {
  if (!isJsonObject(value) || Object.keys(value).sort().join(",") !== "id,runNumber,status" || !isGuid(value.id) || !isSafeCount(value.runNumber, true) || !WORKFLOW_RUN_STATUSES.has(value.status as WorkflowRunStatus)) {
    throw new InvalidWorkflowRunIdentityError();
  }

  return { id: value.id, runNumber: value.runNumber, status: value.status as WorkflowRunStatus };
}

function decodeWorkflowRunPendingWaitObservation(value: unknown, expectedRunId: string): WorkflowRunPendingWaitObservation {
  const invalid = (): never => { throw new InvalidWorkflowRunPendingWaitObservationError(); };
  if (!isJsonObject(value) || Object.keys(value).sort().join(",") !== "runId,wait" || !sameGuid(value.runId, expectedRunId)) return invalid();
  if (value.wait === null) return { runId: value.runId, wait: null };
  const wait = value.wait;
  if (!isJsonObject(wait) || Object.keys(wait).sort().join(",") !== "id,kind,nodeId,promptPrefix,promptState,token,wakeAt"
    || !isGuid(wait.id) || typeof wait.nodeId !== "string" || wait.nodeId.length === 0 || wait.nodeId.length > 128
    || typeof wait.kind !== "string" || wait.kind.length === 0 || wait.kind.length > 24
    || typeof wait.token !== "string" || wait.token.length === 0 || wait.token.length > 128
    || !isNullableString(wait.wakeAt) || wait.wakeAt !== null && !Number.isFinite(Date.parse(wait.wakeAt))
    || !WORKFLOW_RUN_PENDING_WAIT_PROMPT_STATES.has(wait.promptState as WorkflowRunPendingWaitPromptState)
    || !isNullableString(wait.promptPrefix) || wait.promptPrefix !== null && wait.promptPrefix.length > WORKFLOW_RUN_PENDING_WAIT_PROMPT_MAX) return invalid();
  const promptState = wait.promptState as WorkflowRunPendingWaitPromptState;
  if ((promptState === "Missing" || promptState === "Invalid") && wait.promptPrefix !== null || promptState === "Exact" && wait.promptPrefix === null
    || promptState === "Truncated" && (wait.promptPrefix === null || wait.promptPrefix.length !== WORKFLOW_RUN_PENDING_WAIT_PROMPT_MAX)) return invalid();
  return { runId: value.runId, wait: { id: wait.id, nodeId: wait.nodeId, kind: wait.kind, token: wait.token, wakeAt: wait.wakeAt, promptState, promptPrefix: wait.promptPrefix } };
}

function isSafeCount(value: unknown, positive = false): value is number {
  return Number.isSafeInteger(value) && (positive ? Number(value) > 0 : Number(value) >= 0);
}

function invalidRunRecordPageRequest(): never {
  throw new Error("Invalid Workflow Run record page request.");
}

function invalidRunRecordPage(): never {
  throw new InvalidWorkflowRunRecordPageError();
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === "string";
}

function hasNullableString(value: Record<string, unknown>, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(value, key) && isNullableString(value[key]);
}

function isGuid(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

function invalidRunToolCall(): never {
  throw new InvalidWorkflowRunToolCallResponseError();
}

function invalidRunModelCallPage(): never {
  throw new InvalidWorkflowRunModelCallPageError();
}

function sameGuid(left: unknown, right: string): left is string {
  return isGuid(left) && left.toLowerCase() === right.toLowerCase();
}

/** Exact-enough key for PostgreSQL timestamptz JSON (up to nanoseconds), without Date's millisecond collapse. */
function instantKey(value: unknown): bigint | null {
  if (typeof value !== "string") return null;
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) return null;
  const milliseconds = Date.parse(`${match[1]}${match[3]}`);
  if (!Number.isFinite(milliseconds)) return null;
  const nanoseconds = BigInt((match[2] ?? "").padEnd(9, "0"));
  return BigInt(milliseconds) * 1_000_000n + nanoseconds;
}

function decodeRunToolCall(value: unknown, expectedRunId: string): WorkflowRunToolCallMetadata {
  if (!isJsonObject(value) || !isGuid(value.toolCallId) || !sameGuid(value.runId, expectedRunId)
    || typeof value.toolAdapterKind !== "string" || value.toolAdapterKind.length === 0
    || typeof value.toolName !== "string" || value.toolName.length === 0
    || !WORKFLOW_RUN_TOOL_CALL_EFFECTS.has(value.effectClass as WorkflowRunToolCallEffectClass)
    || !WORKFLOW_RUN_TOOL_CALL_STATES.has(value.state as WorkflowRunToolCallObservationState)
    || !isSafeCount(value.callOrdinal, true) || !hasNullableString(value, "sourceKind")
    || (typeof value.sourceKind === "string" && value.sourceKind.length === 0)
    || !Object.prototype.hasOwnProperty.call(value, "sourceCorrelationId")
    || (value.sourceCorrelationId !== null && !isGuid(value.sourceCorrelationId))
    || typeof value.captureSource !== "string" || value.captureSource.length === 0
    || !WORKFLOW_RUN_CAPTURE_COMPLETENESS.has(value.captureCompleteness as WorkflowRunCaptureCompleteness)
    || instantKey(value.createdAt) === null || instantKey(value.lastModifiedAt) === null
    || !Object.prototype.hasOwnProperty.call(value, "terminalAt") || (value.terminalAt !== null && instantKey(value.terminalAt) === null)
    || !Object.prototype.hasOwnProperty.call(value, "errorCode")
    || (value.errorCode !== null && !WORKFLOW_RUN_TOOL_CALL_ERROR_CODES.has(value.errorCode as WorkflowRunToolCallErrorCode)))
    return invalidRunToolCall();

  return {
    toolCallId: value.toolCallId,
    runId: value.runId as string,
    toolAdapterKind: value.toolAdapterKind,
    toolName: value.toolName,
    effectClass: value.effectClass as WorkflowRunToolCallEffectClass,
    state: value.state as WorkflowRunToolCallObservationState,
    callOrdinal: value.callOrdinal,
    sourceKind: value.sourceKind as string | null,
    sourceCorrelationId: value.sourceCorrelationId as string | null,
    captureSource: value.captureSource,
    captureCompleteness: value.captureCompleteness as WorkflowRunCaptureCompleteness,
    createdAt: value.createdAt as string,
    lastModifiedAt: value.lastModifiedAt as string,
    terminalAt: value.terminalAt as string | null,
    errorCode: value.errorCode as WorkflowRunToolCallErrorCode | null,
  };
}

function decodeRunToolCallAttempt(value: unknown): WorkflowRunToolCallAttemptMetadata {
  if (!isJsonObject(value) || !isSafeCount(value.attemptOrdinal, true)
    || !WORKFLOW_RUN_TOOL_CALL_ATTEMPT_STATUSES.has(value.status as WorkflowRunToolCallAttemptStatus)
    || typeof value.captureSource !== "string" || value.captureSource.length === 0
    || !WORKFLOW_RUN_CAPTURE_COMPLETENESS.has(value.captureCompleteness as WorkflowRunCaptureCompleteness)
    || instantKey(value.startedAt) === null
    || !Object.prototype.hasOwnProperty.call(value, "completedAt") || (value.completedAt !== null && instantKey(value.completedAt) === null)
    || instantKey(value.createdAt) === null || instantKey(value.lastModifiedAt) === null
    || !Object.prototype.hasOwnProperty.call(value, "errorCode")
    || (value.errorCode !== null && !WORKFLOW_RUN_TOOL_CALL_ERROR_CODES.has(value.errorCode as WorkflowRunToolCallErrorCode)))
    return invalidRunToolCall();

  return {
    attemptOrdinal: value.attemptOrdinal,
    status: value.status as WorkflowRunToolCallAttemptStatus,
    captureSource: value.captureSource,
    captureCompleteness: value.captureCompleteness as WorkflowRunCaptureCompleteness,
    startedAt: value.startedAt as string,
    completedAt: value.completedAt as string | null,
    createdAt: value.createdAt as string,
    lastModifiedAt: value.lastModifiedAt as string,
    errorCode: value.errorCode as WorkflowRunToolCallErrorCode | null,
  };
}

function decodeRunToolCallPage(value: unknown, expectedRunId: string, request: WorkflowRunToolCallPageRequest): WorkflowRunToolCallPage {
  const expectedCursor = request.cursor ?? null;
  if (!isJsonObject(value) || !sameGuid(value.runId, expectedRunId) || value.requestCursor !== expectedCursor
    || value.limit !== request.limit || !Array.isArray(value.items) || value.items.length > request.limit
    || !Object.prototype.hasOwnProperty.call(value, "nextCursor")
    || (value.nextCursor !== null && (typeof value.nextCursor !== "string" || value.nextCursor.length === 0 || value.nextCursor.length > WORKFLOW_RUN_TOOL_CALL_CURSOR_MAX)))
    return invalidRunToolCall();

  const items = value.items.map((candidate) => decodeRunToolCall(candidate, expectedRunId));
  const ids = new Set<string>();
  let previous: WorkflowRunToolCallMetadata | null = null;
  for (const item of items) {
    const id = item.toolCallId.toLowerCase();
    const instant = instantKey(item.createdAt)!;
    const previousInstant = previous === null ? null : instantKey(previous.createdAt)!;
    if (ids.has(id) || (previous !== null && (previousInstant! < instant || (previousInstant === instant && previous.toolCallId.toLowerCase() <= id))))
      return invalidRunToolCall();
    ids.add(id);
    previous = item;
  }
  if ((items.length === 0 || items.length < request.limit) && value.nextCursor !== null) return invalidRunToolCall();
  return { runId: value.runId as string, requestCursor: expectedCursor, limit: request.limit, items, nextCursor: value.nextCursor as string | null };
}

function decodeRunToolCallDetail(value: unknown, expectedRunId: string, expectedCallId: string): WorkflowRunToolCallDetail {
  if (!isJsonObject(value) || !Array.isArray(value.attempts) || value.attempts.length > WORKFLOW_RUN_TOOL_CALL_ATTEMPT_MAX || typeof value.attemptsTruncated !== "boolean")
    return invalidRunToolCall();
  const call = decodeRunToolCall(value.call, expectedRunId);
  if (!sameGuid(call.toolCallId, expectedCallId)) return invalidRunToolCall();
  const attempts = value.attempts.map(decodeRunToolCallAttempt);
  let previousOrdinal = 0;
  for (const attempt of attempts) {
    if (attempt.attemptOrdinal <= previousOrdinal) return invalidRunToolCall();
    previousOrdinal = attempt.attemptOrdinal;
  }
  if (value.attemptsTruncated && attempts.length !== WORKFLOW_RUN_TOOL_CALL_ATTEMPT_MAX) return invalidRunToolCall();
  return { call, attempts, attemptsTruncated: value.attemptsTruncated };
}

function decodeRunModelCallPage(value: unknown, expectedRunId: string, expectedCursor: string | null, expectedLimit: number): WorkflowRunModelCallPage {
  if (!isJsonObject(value) || !sameGuid(value.runId, expectedRunId) || value.requestCursor !== expectedCursor || value.limit !== expectedLimit
    || !Array.isArray(value.items) || value.items.length > expectedLimit || !Object.prototype.hasOwnProperty.call(value, "nextCursor")
    || (value.nextCursor !== null && (typeof value.nextCursor !== "string" || value.nextCursor.length === 0 || value.nextCursor.length > WORKFLOW_RUN_MODEL_CALL_CURSOR_MAX)))
    return invalidRunModelCallPage();

  const items: WorkflowRunModelCallListItem[] = [];
  const ids = new Set<string>();
  let previous: WorkflowRunModelCallListItem | null = null;
  for (const candidate of value.items) {
    if (!isJsonObject(candidate) || !isGuid(candidate.workflowRunModelCallId) || !sameGuid(candidate.runId, expectedRunId)
      || !isSafeCount(candidate.callOrdinal, true) || !hasNullableString(candidate, "nodeId") || typeof candidate.iterationKey !== "string"
      || !hasNullableString(candidate, "executionAttemptId") || candidate.executionAttemptId !== null && !isGuid(candidate.executionAttemptId)
      || typeof candidate.purpose !== "string" || candidate.purpose.length === 0 || !hasNullableString(candidate, "requestedProvider")
      || !hasNullableString(candidate, "requestedModel") || typeof candidate.captureSource !== "string" || candidate.captureSource.length === 0
      || !WORKFLOW_RUN_CAPTURE_COMPLETENESS.has(candidate.captureCompleteness as WorkflowRunCaptureCompleteness) || instantKey(candidate.createdAt) === null)
      return invalidRunModelCallPage();
    const item = candidate as unknown as WorkflowRunModelCallListItem;
    const id = item.workflowRunModelCallId.toLowerCase();
    const instant = instantKey(item.createdAt)!;
    const previousInstant = previous === null ? null : instantKey(previous.createdAt)!;
    if (ids.has(id) || previous !== null && (previousInstant! < instant || previousInstant === instant && previous.workflowRunModelCallId.toLowerCase() <= id))
      return invalidRunModelCallPage();
    ids.add(id);
    items.push(item);
    previous = item;
  }

  if ((items.length === 0 || items.length < expectedLimit) && value.nextCursor !== null) return invalidRunModelCallPage();
  return { runId: value.runId, requestCursor: expectedCursor, limit: expectedLimit, items, nextCursor: value.nextCursor as string | null };
}

function decodeRunRecordPage(value: unknown, expectedRunId: string, request: RunRecordPageRequest): RunRecordPageResponse {
  const expectedMode: RunRecordPageMode = request.afterSequence !== undefined ? "Newer" : request.beforeSequence !== undefined ? "Older" : "Tail";
  if (!isJsonObject(value) || value.runId !== expectedRunId || value.mode !== expectedMode || !RUN_RECORD_PAGE_MODES.has(value.mode as RunRecordPageMode) || !WORKFLOW_RUN_STATUSES.has(value.runStatus as WorkflowRunStatus) || !Array.isArray(value.records) || value.records.length > request.limit)
    return invalidRunRecordPage();

  const nextBefore = value.nextBeforeSequence;
  const nextAfter = value.nextAfterSequence;
  if ((nextBefore !== null && !isSafeCount(nextBefore, true)) || (nextAfter !== null && !isSafeCount(nextAfter, true))) return invalidRunRecordPage();
  if ((expectedMode === "Newer" && nextBefore !== null) || (expectedMode !== "Newer" && nextAfter !== null)) return invalidRunRecordPage();

  const records: RunRecordPageItem[] = [];
  const recordIds = new Set<string>();
  let previousSequence = 0;
  for (const candidate of value.records) {
    if (!isJsonObject(candidate) || Object.prototype.hasOwnProperty.call(candidate, "payloadJson") || !isGuid(candidate.recordId) || recordIds.has(candidate.recordId.toLowerCase()) || !isSafeCount(candidate.sequence, true) || candidate.sequence <= previousSequence || typeof candidate.recordType !== "string" || candidate.recordType.length === 0 || !hasNullableString(candidate, "nodeId") || typeof candidate.iterationKey !== "string" || typeof candidate.occurredAt !== "string" || !Number.isFinite(Date.parse(candidate.occurredAt)) || candidate.payloadState !== "Deferred" || candidate.payloadContentType !== "application/json" || !hasNullableString(candidate, "correlationId") || !hasNullableString(candidate, "parentRecordId"))
      return invalidRunRecordPage();
    if (request.beforeSequence !== undefined && candidate.sequence >= request.beforeSequence) return invalidRunRecordPage();
    if (request.afterSequence !== undefined && candidate.sequence <= request.afterSequence) return invalidRunRecordPage();

    records.push({
      recordId: candidate.recordId,
      sequence: candidate.sequence,
      recordType: candidate.recordType,
      nodeId: candidate.nodeId as string | null,
      iterationKey: candidate.iterationKey,
      occurredAt: candidate.occurredAt,
      payloadState: "Deferred",
      payloadContentType: "application/json",
      correlationId: candidate.correlationId as string | null,
      parentRecordId: candidate.parentRecordId as string | null,
    });
    recordIds.add(candidate.recordId.toLowerCase());
    previousSequence = candidate.sequence;
  }

  if ((nextBefore !== null && (records.length === 0 || nextBefore !== records[0].sequence)) || (nextAfter !== null && (records.length === 0 || nextAfter !== records.at(-1)?.sequence)))
    return invalidRunRecordPage();

  return {
    runId: expectedRunId,
    runStatus: value.runStatus as WorkflowRunStatus,
    mode: expectedMode,
    records,
    nextBeforeSequence: nextBefore as number | null,
    nextAfterSequence: nextAfter as number | null,
  };
}

interface ExpectedRunRecordPayloadRange {
  runId: string;
  recordId: string;
  sequence: number;
  offsetBytes: number;
  limitBytes: number;
}

function decodeRunRecordPayloadRange(headers: Headers, bytes: Uint8Array, expected: ExpectedRunRecordPayloadRange): RunRecordPayloadRangeResult {
  const runId = headers.get("X-CodeSpace-Workflow-Run-Id");
  const recordId = headers.get("X-CodeSpace-Workflow-Run-Record-Id");
  const sequence = exactUnsignedIntegerHeader(headers, "X-CodeSpace-Workflow-Run-Record-Sequence");
  const offsetBytes = exactUnsignedIntegerHeader(headers, "X-CodeSpace-Workflow-Run-Record-Payload-Offset");
  const nextRaw = headers.get("X-CodeSpace-Workflow-Run-Record-Payload-Next-Offset");
  const nextOffsetBytes = nextRaw == null ? null : exactUnsignedIntegerHeader(headers, "X-CodeSpace-Workflow-Run-Record-Payload-Next-Offset");
  const totalBytes = exactUnsignedIntegerHeader(headers, "X-CodeSpace-Workflow-Run-Record-Payload-Total-Bytes");
  const contentType = headers.get("X-CodeSpace-Workflow-Run-Record-Payload-Content-Type");
  const transportType = headers.get("Content-Type")?.split(";", 1)[0].trim().toLowerCase();
  const computedNext = offsetBytes == null ? null : offsetBytes + bytes.byteLength;
  const hasMore = computedNext != null && totalBytes != null && computedNext < totalBytes;
  const valid = runId?.toLowerCase() === expected.runId.toLowerCase() && recordId?.toLowerCase() === expected.recordId.toLowerCase()
    && sequence === expected.sequence && offsetBytes === expected.offsetBytes && bytes.byteLength <= expected.limitBytes
    && computedNext != null && Number.isSafeInteger(computedNext) && totalBytes != null && computedNext <= totalBytes
    && !(bytes.byteLength === 0 && offsetBytes < totalBytes) && (hasMore ? nextRaw != null && nextOffsetBytes === computedNext : nextRaw == null)
    && contentType === "application/json" && transportType === "application/octet-stream";
  if (!valid) return { availability: "InvalidResponse", code: "invalid_record_payload_range_headers", isRetryable: false };
  return {
    availability: "Available", bytes, runId: expected.runId, recordId: expected.recordId, sequence: expected.sequence,
    offsetBytes: expected.offsetBytes, nextOffsetBytes, totalBytes, contentType: "application/json",
  };
}

function decodeRunRecordPayloadProblem(value: unknown, expected: Pick<ExpectedRunRecordPayloadRange, "runId" | "recordId" | "sequence">): RunRecordPayloadRangeProblem {
  if (!isJsonObject(value) || value.runId !== expected.runId || value.recordId !== expected.recordId || value.sequence !== expected.sequence
    || value.availability !== "InvalidRange" || typeof value.code !== "string" || value.code.length === 0 || value.isRetryable !== false)
    return { availability: "InvalidResponse", code: "invalid_record_payload_problem", isRetryable: false };
  return { availability: "InvalidRange", code: value.code, isRetryable: false };
}

function exactUnsignedIntegerHeader(headers: Headers, name: string): number | null {
  const raw = headers.get(name);
  if (raw == null || !/^\d+$/.test(raw)) return null;
  const parsed = Number(raw);
  return Number.isSafeInteger(parsed) ? parsed : null;
}

async function readBoundedBytes(response: Response, maximumBytes: number): Promise<Uint8Array | null> {
  const declaredLength = response.headers.get("Content-Length");
  const expectedLength = declaredLength == null || !/^\d+$/.test(declaredLength) ? null : Number(declaredLength);
  if (declaredLength != null && (expectedLength == null || expectedLength > maximumBytes)) return null;
  if (response.body == null) return expectedLength == null || expectedLength === 0 ? new Uint8Array() : null;

  const chunks: Uint8Array[] = [];
  let total = 0;
  const reader = response.body.getReader();
  try {
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      total += next.value.byteLength;
      if (total > maximumBytes) {
        await reader.cancel();
        return null;
      }
      chunks.push(next.value);
    }
  } finally {
    reader.releaseLock();
  }

  if (expectedLength != null && expectedLength !== total) return null;

  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return bytes;
}

function invalidRunDataCompleteness(): never {
  throw new Error("Invalid Workflow Run data completeness response.");
}

function decodeRunDataCompleteness(value: unknown, expectedRunId: string): WorkflowRunDataCompletenessView {
  if (!isJsonObject(value) || value.runId !== expectedRunId || value.scope !== "RecordedFacetsOnly"
    || (value.runWideVerdict !== null && !WORKFLOW_RUN_CAPTURE_COMPLETENESS.has(value.runWideVerdict as WorkflowRunCaptureCompleteness))
    || typeof value.hasStatements !== "boolean" || typeof value.isTerminal !== "boolean" || typeof value.truncated !== "boolean"
    || !Array.isArray(value.requiredFacets) || !Array.isArray(value.missingFacetStatements) || !Array.isArray(value.facets) || value.facets.length > 100)
    return invalidRunDataCompleteness();

  const requiredFacets = value.requiredFacets.filter((facet): facet is string => typeof facet === "string" && facet.length > 0);
  const missingFacetStatements = value.missingFacetStatements.filter((facet): facet is string => typeof facet === "string" && facet.length > 0);
  if (requiredFacets.length !== value.requiredFacets.length || missingFacetStatements.length !== value.missingFacetStatements.length
    || new Set(requiredFacets).size !== requiredFacets.length || new Set(missingFacetStatements).size !== missingFacetStatements.length
    || missingFacetStatements.some((facet) => !requiredFacets.includes(facet))) return invalidRunDataCompleteness();

  const facets: WorkflowRunDataFacetCompleteness[] = [];
  let previousFacet: string | null = null;

  for (const candidate of value.facets) {
    if (!isJsonObject(candidate) || typeof candidate.facet !== "string" || candidate.facet.length === 0 || (previousFacet !== null && candidate.facet <= previousFacet) || !WORKFLOW_RUN_CAPTURE_COMPLETENESS.has(candidate.verdict as WorkflowRunCaptureCompleteness) || typeof candidate.isStrictlyReadable !== "boolean" || !isSafeCount(candidate.presentRecordCount) || !isSafeCount(candidate.knownMissingCount) || (candidate.expectedRecordCount !== null && !isSafeCount(candidate.expectedRecordCount)) || !isSafeCount(candidate.revision, true) || !isSafeCount(candidate.schemaVersion, true) || typeof candidate.lastModifiedAt !== "string" || !Number.isFinite(Date.parse(candidate.lastModifiedAt)))
      return invalidRunDataCompleteness();

    const verdict = candidate.verdict as WorkflowRunCaptureCompleteness;
    if (candidate.isStrictlyReadable !== (verdict === "Exact" || verdict === "RedactedExact")) return invalidRunDataCompleteness();

    facets.push({
      facet: candidate.facet,
      expectedRecordCount: candidate.expectedRecordCount as number | null,
      presentRecordCount: candidate.presentRecordCount,
      knownMissingCount: candidate.knownMissingCount,
      verdict,
      isStrictlyReadable: candidate.isStrictlyReadable,
      revision: candidate.revision,
      schemaVersion: candidate.schemaVersion,
      lastModifiedAt: candidate.lastModifiedAt,
    });
    previousFacet = candidate.facet;
  }

  const facetNames = new Set(facets.map((facet) => facet.facet));
  const expectedMissing = requiredFacets.filter((facet) => !facetNames.has(facet));
  if (value.hasStatements !== (facets.length > 0) || expectedMissing.length !== missingFacetStatements.length
    || expectedMissing.some((facet) => !missingFacetStatements.includes(facet))
    || value.runWideVerdict !== null && (!value.isTerminal || value.truncated || missingFacetStatements.length > 0)) return invalidRunDataCompleteness();

  return { runId: expectedRunId, scope: "RecordedFacetsOnly", facets, hasStatements: value.hasStatements, isTerminal: value.isTerminal,
    requiredFacets, missingFacetStatements, runWideVerdict: value.runWideVerdict as WorkflowRunCaptureCompleteness | null, truncated: value.truncated };
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
  getRun: (ref: string, signal?: AbortSignal) => fetchJson<WorkflowRunDetail>(`/api/workflows/runs/${encodeURIComponent(ref)}`, { signal }),

  /** Resolve only the canonical identity needed by the run route; never loads execution detail or artifact bytes. */
  getRunIdentity: async (ref: string, signal?: AbortSignal) => decodeWorkflowRunIdentity(await fetchJson<unknown>(`/api/workflows/runs/${encodeURIComponent(ref)}/identity`, { signal })),

  /** Read only the current pending action and a capped approval-prompt prefix; never materializes the wait payload. */
  getRunPendingWait: async (runId: string, signal?: AbortSignal): Promise<WorkflowRunPendingWaitObservation | null> => {
    try {
      return decodeWorkflowRunPendingWaitObservation(await fetchJson<unknown>(`/api/workflows/runs/${runId}/pending-wait`, { signal }), runId);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

  /** Bounded producer statements only; no record/blob read and no synthesized run-wide verdict. */
  getRunDataCompleteness: async (runId: string, signal?: AbortSignal): Promise<WorkflowRunDataCompletenessView | null> => {
    try {
      return decodeRunDataCompleteness(await fetchJson<unknown>(`/api/workflows/runs/${runId}/data-completeness`, { signal }), runId);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

  /** One metadata-only CreatedAt+id keyset page of terminal governed side-effect observations. */
  pageRunToolCalls: async (runId: string, request: WorkflowRunToolCallPageRequest, signal?: AbortSignal): Promise<WorkflowRunToolCallPage | null> => {
    if (!isGuid(runId) || !Number.isSafeInteger(request.limit) || request.limit < 1 || request.limit > WORKFLOW_RUN_TOOL_CALL_PAGE_MAX
      || (request.cursor !== undefined && (request.cursor.length === 0 || request.cursor.trim() !== request.cursor || request.cursor.length > WORKFLOW_RUN_TOOL_CALL_CURSOR_MAX)))
      return invalidRunToolCall();
    const params = new URLSearchParams({ limit: String(request.limit) });
    if (request.cursor !== undefined) params.set("cursor", request.cursor);
    try {
      const value = await fetchJson<unknown>(`/api/workflows/runs/${encodeURIComponent(runId)}/tool-calls?${params}`, { signal });
      return decodeRunToolCallPage(value, runId, request);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

  /** One stable call's metadata plus at most 100 ordered metadata-only attempts. */
  getRunToolCall: async (runId: string, toolCallId: string, signal?: AbortSignal): Promise<WorkflowRunToolCallDetail | null> => {
    if (!isGuid(runId) || !isGuid(toolCallId)) return invalidRunToolCall();
    try {
      const value = await fetchJson<unknown>(`/api/workflows/runs/${encodeURIComponent(runId)}/tool-calls/${encodeURIComponent(toolCallId)}`, { signal });
      return decodeRunToolCallDetail(value, runId, toolCallId);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

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

  /** One strict, bounded, body-free keyset page of the raw ledger. */
  getRunRecordPage: async (runId: string, request: RunRecordPageRequest, signal?: AbortSignal): Promise<RunRecordPageResponse> => {
    const validBefore = request.beforeSequence === undefined || isSafeCount(request.beforeSequence, true);
    const validAfter = request.afterSequence === undefined || isSafeCount(request.afterSequence);
    if (!validBefore || !validAfter || (request.beforeSequence !== undefined && request.afterSequence !== undefined) || !Number.isSafeInteger(request.limit) || request.limit < 1 || request.limit > 500)
      return invalidRunRecordPageRequest();

    const params = new URLSearchParams({ limit: String(request.limit) });
    if (request.beforeSequence !== undefined) params.set("beforeSequence", String(request.beforeSequence));
    if (request.afterSequence !== undefined) params.set("afterSequence", String(request.afterSequence));
    const value = await fetchJson<unknown>(`/api/workflows/runs/${encodeURIComponent(runId)}/records/page?${params}`, { signal });
    return decodeRunRecordPage(value, runId, request);
  },

  /** Exact record-scoped canonical JSONB bytes. The response body is consumed with a client-side hard cap too. */
  readRunRecordPayloadRange: async (runId: string, recordId: string, sequence: number, offsetBytes: number, limitBytes: number, signal?: AbortSignal): Promise<RunRecordPayloadRangeResult> => {
    if (!isGuid(runId) || !isGuid(recordId) || !isSafeCount(sequence, true) || !isSafeCount(offsetBytes) || !Number.isSafeInteger(limitBytes) || limitBytes < 1 || limitBytes > 64 * 1024 || offsetBytes > Number.MAX_SAFE_INTEGER - limitBytes)
      return { availability: "InvalidResponse", code: "invalid_record_payload_range_request", isRetryable: false };

    const path = `/api/workflows/runs/${encodeURIComponent(runId)}/records/${encodeURIComponent(recordId)}/payload?offsetBytes=${offsetBytes}&limitBytes=${limitBytes}`;
    try {
      const response = await fetchResponse(path, { signal, headers: { Accept: "application/octet-stream" } });
      const bytes = await readBoundedBytes(response, limitBytes);
      if (bytes == null) return { availability: "InvalidResponse", code: "invalid_record_payload_body_length", isRetryable: false };
      return decodeRunRecordPayloadRange(response.headers, bytes, { runId, recordId, sequence, offsetBytes, limitBytes });
    } catch (error) {
      if (error instanceof Error && error.name === "AbortError") throw error;
      if (!(error instanceof ApiError)) return { availability: "BackendUnavailable", code: "transport_unavailable", isRetryable: true };
      if (error.status === 404) return { availability: "Missing", code: error.code, isRetryable: false };
      if (error.status === 401 || error.status === 403) return { availability: "AccessDenied", code: error.code, isRetryable: false };
      if (error.status === 408 || error.status === 429 || error.status >= 500) return { availability: "BackendUnavailable", code: error.code, isRetryable: true };
      return decodeRunRecordPayloadProblem(error.body, { runId, recordId, sequence });
    }
  },

  /** Metadata only; opening the drawer never eagerly downloads prompt/result blobs. */
  pageRunModelCalls: async (runId: string, cursor?: string, limit = 100, signal?: AbortSignal): Promise<WorkflowRunModelCallPage | null> => {
    if (!isGuid(runId) || !Number.isSafeInteger(limit) || limit < 1 || limit > WORKFLOW_RUN_MODEL_CALL_PAGE_MAX
      || cursor !== undefined && (cursor.length === 0 || cursor.trim() !== cursor || cursor.length > WORKFLOW_RUN_MODEL_CALL_CURSOR_MAX))
      return invalidRunModelCallPage();
    const params = new URLSearchParams({ limit: String(limit) });
    if (cursor) params.set("cursor", cursor);
    try {
      const value = await fetchJson<unknown>(`/api/workflows/runs/${encodeURIComponent(runId)}/model-calls?${params}`, { signal });
      return decodeRunModelCallPage(value, runId, cursor ?? null, limit);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

  /** Metadata only; opening the drawer never eagerly downloads prompt/result blobs. */
  getRunModelCall: async (runId: string, sequence: number, signal?: AbortSignal): Promise<WorkflowRunModelCallMetadata | null> => {
    try {
      return await fetchJson<WorkflowRunModelCallMetadata>(`/api/workflows/runs/${runId}/model-calls/${sequence}`, { signal });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

  /** Byte-free stable logical call and physical-attempt metadata. */
  getRunModelCallById: async (runId: string, modelCallId: string, signal?: AbortSignal): Promise<WorkflowRunModelCallDetailMetadata | null> => {
    try {
      return await fetchJson<WorkflowRunModelCallDetailMetadata>(`/api/workflows/runs/${runId}/model-calls/${modelCallId}`, { signal });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

  /** One bounded UTF-8 page from a stable logical-call or physical-attempt body reference. */
  getRunModelCallBody: async (runId: string, modelCallId: string, read: WorkflowRunModelCallBodyRead, signal?: AbortSignal): Promise<WorkflowRunModelCallBodyPage | null> => {
    const params = new URLSearchParams({ offsetBytes: String(read.offsetBytes), limitBytes: String(read.limitBytes) });
    if (read.attemptId) params.set("attemptId", read.attemptId);
    try {
      return await fetchJson<WorkflowRunModelCallBodyPage>(`/api/workflows/runs/${runId}/model-calls/${modelCallId}/bodies/${read.body}?${params}`, { signal });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

  /** One bounded UTF-8 page of a selected Workflow Run model-call part. */
  getRunModelCallPart: async (runId: string, sequence: number, part: WorkflowRunModelCallPart, offsetBytes: number, limitBytes: number, signal?: AbortSignal): Promise<WorkflowRunModelCallPartPage | null> => {
    try {
      return await fetchJson<WorkflowRunModelCallPartPage>(`/api/workflows/runs/${runId}/model-calls/${sequence}/parts/${part}?offsetBytes=${offsetBytes}&limitBytes=${limitBytes}`, { signal });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },

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
