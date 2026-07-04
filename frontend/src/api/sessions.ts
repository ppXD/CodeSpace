import { ApiError, fetchJson } from "./request";
import type { WorkflowRunStatus } from "./workflows";

// ─── Types (mirror backend DTOs in CodeSpace.Messages.Dtos.Sessions) ───

/// Lifecycle of a work thread. Mirrors backend `WorkSessionStatus`.
export type WorkSessionStatus = "Open" | "Archived";

/// What type of work a thread solves. Mirrors backend `WorkSessionKind` — an open string for forward-compatibility
/// with new kinds (Task / Pr / Issue / Workflow / Schedule / Custom today).
export type WorkSessionKind = string;

/// One row of the sessions index. Mirrors backend `SessionSummary`.
export interface SessionSummary {
  id: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  turnCount: number;
  createdDate: string;
  lastActivityAt: string;
  latestRunId?: string | null;
  latestRunStatus?: WorkflowRunStatus | null;
  latestProjectionKind?: string | null;
  hasPendingDecision: boolean;
}

/// One keyset page of the sessions index. Mirrors backend `SessionPage`.
export interface SessionPage {
  items: SessionSummary[];
  nextCursor?: string | null;
}

/// One repo's produced branch within a multi-repo turn. Mirrors backend `SessionTurnRepoResult`.
export interface SessionTurnRepoResult {
  repositoryId: string;
  producedBranch: string;
}

/// One attempt (a replay/rerun fork) of a turn. Mirrors backend `SessionTurnAttempt`.
export interface SessionTurnAttempt {
  runId: string;
  attemptNumber: number;
  status: WorkflowRunStatus;
  sourceType: string;
  rerunFromNodeId?: string | null;
  createdDate: string;
  isLatest: boolean;
}

/// One turn of a thread (a top-level run shown like a chat exchange). Mirrors backend `SessionTurn`.
export interface SessionTurn {
  turnIndex: number;
  turnRunId: string;
  runId: string;
  userMessage?: string | null;
  runStatus: WorkflowRunStatus;
  projectionKind?: string | null;
  result?: string | null;
  producedBranch?: string | null;
  repositoryResults?: SessionTurnRepoResult[] | null;
  hasPendingDecision: boolean;
  createdDate: string;
  startedAt?: string | null;
  completedAt?: string | null;
  error?: string | null;
  attemptCount: number;
  attempts?: SessionTurnAttempt[] | null;
}

/// One thread as a conversation. Mirrors backend `SessionDetail`.
export interface SessionDetail {
  id: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  createdDate: string;
  summary?: string | null;
  summaryThroughTurnIndex?: number | null;
  /// When entered by a run id, the turn that run belongs to (the UI scrolls to it). Null when entered by session id.
  anchorTurnIndex?: number | null;
  turns: SessionTurn[];
}

// ─── Session Room (the backend-authored AI work transcript) ───
// Mirrors backend CodeSpace.Messages.Dtos.Sessions.Room. The frontend renders blocks by `type` and owns no
// copy / order / status — an UNKNOWN `type` falls to a generic renderer, so a new backend block kind needs zero
// frontend change (forward-compatible, matching the backend's additive [JsonDerivedType]).

export type ExecutionStepStatus = "Pending" | "Queued" | "Running" | "Done" | "Failed" | "Blocked" | "Skipped";
export type NarrativeTone = "Info" | "Success" | "Error";
export type RoomActionKind = "RerunTurn" | "RerunFromNode" | "RerunFailedMapItems" | "RetryFailedAgent" | "AnswerDecision" | "Stop" | "OpenTrace" | "FixCredentials";

/// A capability-aware action on a turn. `enabled` + `disabledReason` come from the backend, so a click never 422s.
export interface RoomAction {
  kind: RoomActionKind;
  label: string;
  enabled: boolean;
  disabledReason?: string | null;
  target?: string | null;
  attempt?: boolean;
}

export interface ExecutionMapStep {
  id: string;
  label: string;
  status: ExecutionStepStatus;
  /// A short per-step detail under the label — "8s" / "3 agents" / "passed" / "1 of 2" / "skipped". Null when none.
  detail?: string | null;
}

export interface StatItem {
  text: string;
  detail?: string | null;
  tone?: NarrativeTone | null;
}

/// One agent in a group — `agentRunId` is enough to drive the live terminal (it self-polls).
export interface RoomAgentCard {
  agentRunId: string;
  label: string;
  role?: string | null;
  /// The planned subtask this agent was assigned (the model's decomposition). Null for a non-supervisor / homogeneous spawn.
  assignedSubtask?: string | null;
  status: string;
  model?: string | null;
  /// The harness the agent ran on (e.g. "codex-cli" / "claude-code") — the small harness glyph on the card. Null when unknown.
  harness?: string | null;
  tokens?: number | null;
  costUsd?: number | null;
  filesChanged?: number | null;
  /// This agent's OWN changed-file paths (bounded) — per-agent attribution so the UI shows WHICH agent produced a file
  /// (open the agent → preview its exact version). Empty for an agent that changed nothing.
  changedFiles?: string[] | null;
  /// Tool calls the agent made — for the card meta "3 files · 6 tool calls · 41s". Null when unknown.
  toolCount?: number | null;
  /// Wall-clock for the agent — final once terminal, else live elapsed. Null before it starts.
  durationMs?: number | null;
  /// The agent's own one-line result takeaway (what it concluded) — shown on the card before any raw log.
  summary?: string | null;
  latestLine?: string | null;
  /// The workflow node + iteration this agent ran as (the cell key) — lets the opened terminal fetch this cell's attempt
  /// history and switch between attempts, like Activity. Null for a supervisor-spawned agent (no workflow cell to switch).
  nodeId?: string | null;
  iterationKey?: string | null;
}

export interface RoomDecisionOption {
  id: string;
  label: string;
  sideEffecting?: boolean;
}

interface RoomBlockBase {
  id: string;
  seq: number;
}

export interface UserMessageBlock extends RoomBlockBase { type: "user_message"; text: string; at?: string | null; }
export interface ExecutionMapBlock extends RoomBlockBase { type: "execution_map"; steps: ExecutionMapStep[]; }
export interface NarrativeStepBlock extends RoomBlockBase { type: "narrative_step"; text: string; tone?: NarrativeTone; at?: string | null; detail?: string | null; }
export interface AgentGroupBlock extends RoomBlockBase { type: "agent_group"; title: string; agents: RoomAgentCard[]; }
/// A collapsible stat row — one generic block for subtasks / files / tools / reasoning (the projector fills kind/label/detail/items).
export interface StatBlock extends RoomBlockBase { type: "stat"; kind: string; label: string; detail?: string | null; items?: StatItem[] | null; }
/// One checkable line of the plan checklist — the item's contract plus its tape-derived execution state.
export interface PlanChecklistItem {
  ordinal: number;
  itemId: string;
  title: string;
  kind?: string | null;
  /// WorkPlanItemStates value (open vocabulary): Pending / InProgress / Completed / Failed / NeedsReview.
  state: string;
  /// 1-based ordinals of the items this one depends on — rendered "after #1, #3".
  dependsOn?: number[] | null;
  acceptanceLabel?: string | null;
  /// "TestsPass" (command chip) or "ArtifactPresent" (deliverable chip) — picks the chip icon.
  acceptanceKind?: string | null;
  acceptancePassed?: boolean | null;
  acceptanceDetail?: string | null;
  acceptanceCriteria?: string[] | null;
  agentRunId?: string | null;
  attempts: number;
}
export interface RoomPlanQuestionOption { id: string; label: string; recommended?: boolean; }
/// A planner-authored operator question — interactive while the plan awaits confirmation, read-only after.
export interface RoomPlanQuestion { id: string; question: string; options?: RoomPlanQuestionOption[] | null; allowFreeText?: boolean; }
/// The run's durable plan as a live checklist — the whole current version with per-item execution state.
export interface PlanChecklistBlock extends RoomBlockBase {
  type: "plan_checklist";
  label: string;
  version: number;
  status: string;
  detail?: string | null;
  items: PlanChecklistItem[];
  assumptions?: string[] | null;
  questions?: RoomPlanQuestion[] | null;
  hasPriorVersions?: boolean;
}
/// The outcome of answering a pending plan-confirmation card (S3 gate). `resumed` is false when a concurrent
/// answer won the wait first (first answer wins).
export interface WorkPlanConfirmationOutcome { resumed: boolean; approved: boolean; }
/// The delivered change set (PR card).
export interface DeliveryBlock extends RoomBlockBase {
  type: "delivery";
  title: string;
  reference?: string | null;
  branchHead?: string | null;
  branchBase?: string | null;
  checks?: string | null;
  checksOk?: boolean | null;
  url?: string | null;
}
export interface DiagnosticBlock extends RoomBlockBase {
  type: "diagnostic";
  tone?: NarrativeTone;
  title?: string | null;
  text: string;
  actions?: RoomAction[] | null;
  rawDetail?: string | null;
}
export interface DecisionBlock extends RoomBlockBase {
  type: "decision";
  decisionId: string;
  question: string;
  shape: string;
  options?: RoomDecisionOption[] | null;
  risk?: string | null;
  deadline?: string | null;
}
export type AnswerAttachmentKind = "Image" | "FileLink" | "Pr";
/// One typed attachment of the final answer — an image, a file link, or the PR.
export interface AnswerAttachment {
  kind: AnswerAttachmentKind;
  label: string;
  url?: string | null;
  previewUrl?: string | null;
  downloadUrl?: string | null;
  /// For a file: the run id of the agent that PRODUCED it — the preview opens THAT agent's exact version.
  agentRunId?: string | null;
  /// For a file: a short label of the producing agent (its role / subtask) — the "· from <agent>" provenance cue.
  producer?: string | null;
}
/// The turn's rich final result — closing text + typed attachments (files / PR / images), rendered distinctly.
export interface FinalAnswerBlock extends RoomBlockBase {
  type: "final_answer";
  text?: string | null;
  attachments?: AnswerAttachment[] | null;
}
/// A live "working…" line pinned at the bottom of an active turn (latest public activity, never raw CoT).
export interface LiveActivityBlock extends RoomBlockBase {
  type: "live_activity";
  text: string;
  agentRunId?: string | null;
}
export interface AssistantTurnBlock extends RoomBlockBase {
  type: "assistant_turn";
  turnIndex: number;
  turnRunId: string;
  runId: string;
  status: WorkflowRunStatus;
  summary?: string | null;
  map?: ExecutionMapBlock | null;
  blocks: RoomBlock[];
  actions: RoomAction[];
  at?: string | null;
  /// Wall-clock so far — final once terminal, else live elapsed. Null before it starts.
  durationMs?: number | null;
  /// The turn's rerun/replay attempts (oldest → newest) — the header's "N attempts" timeline. Empty for a never-rerun turn.
  attempts?: RoomTurnAttempt[];
}

/// One attempt of a turn (the original + each rerun/replay fork). Mirrors backend `RoomTurnAttempt`.
export interface RoomTurnAttempt {
  runId: string;
  attemptNumber: number;
  status: WorkflowRunStatus;
  at: string;
  /// The attempt the turn currently shows (the newest) — rendered as "shown", not an open link.
  isCurrent: boolean;
}

export type RoomBlock =
  | UserMessageBlock
  | AssistantTurnBlock
  | ExecutionMapBlock
  | NarrativeStepBlock
  | AgentGroupBlock
  | StatBlock
  | PlanChecklistBlock
  | DeliveryBlock
  | DecisionBlock
  | DiagnosticBlock
  | FinalAnswerBlock
  | LiveActivityBlock;

/// A generic preview of one file a turn produced — resolved backend-side from the producing agent's captured diff.
/// The frontend renders by `kind`: `text` (full content), `diff` (unified-diff section), `binary` / `unavailable`
/// (a notice + optional source link). Mirrors backend `RoomFilePreview`.
export interface RoomFilePreview {
  path: string;
  kind: "text" | "diff" | "binary" | "unavailable";
  changeKind?: string | null;
  text?: string | null;
  sizeBytes?: number | null;
  truncated: boolean;
  sourceUrl?: string | null;
  note?: string | null;
}

/// One session as a backend-authored transcript. Mirrors backend `RoomView`.
export interface RoomView {
  sessionId: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  cursor: number;
  anchorBlockId?: string | null;
  blocks: RoomBlock[];
}

// ═══ Session Journal — the chronological work transcript (the new /journal surface, built alongside the room) ═══

/// A journal step's render tone — the timeline's closed severity axis. Mirrors backend `TimelineSeverity`.
export type JournalTone = "Info" | "Success" | "Warning" | "Error";

/// One changed file with its +added / −removed line counts (git ground truth; a binary file's counts are null). Mirrors backend `FileDiffStat`.
export interface JournalFileStat {
  path: string;
  additions?: number | null;
  deletions?: number | null;
}

/// One agent a supervisor decision spawned / re-ran — the card the journal hangs off a spawn step. Mirrors backend `JournalAgentCard`.
export interface JournalAgentCard {
  agentRunId: string;
  label: string;
  /// The human-readable planned subtask title — shown on hover over the (slug) label + in the drawer strip, so the
  /// readable title isn't lost when the header is the id. Null for a non-supervisor / homogeneous agent.
  assignedSubtask?: string | null;
  status: string;
  model?: string | null;
  /// The harness the agent ran on (e.g. "codex-cli" / "claude-code") — the small harness glyph on the card. Null when unknown.
  harness?: string | null;
  durationMs?: number | null;
  tokens?: number | null;
  toolCount?: number | null;
  costUsd?: number | null;
  filesChanged?: number | null;
  files: JournalFileStat[];
  resumed: boolean;
}

/// A planned subtask still blocked by an unmet dependency at a wave (the "waiting on #n"). Mirrors backend `JournalDeferredSubtask`.
export interface JournalDeferredSubtask {
  subtaskId: string;
  waitingOn: string[];
}

/// One planned subtask on a PLAN step — the model's authored plan, rendered inline under "planned the work". Mirrors backend `JournalSubtask`.
export interface JournalSubtask {
  subtaskId: string;
  title: string;
}

/// One chronological step of a run's work journal — the frontend renders by `kind`. Mirrors backend `JournalStep`.
/// The structured facts of one model call — mirrors backend `JournalModelCall`. Rendered as a row in the expanded model
/// fold (purpose · model · tokens · latency · cost · status). Cost/latency/tokens are null when unknown (unpriced model,
/// unpaired start, usage-silent call).
export interface JournalModelCall {
  purpose: string;
  model?: string | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  tokens?: number | null;
  latencyMs?: number | null;
  costUsd?: number | null;
  status: string;
}

export interface JournalStep {
  id: string;
  cursor: string;
  at: string;
  kind: string;
  /// Whether this step is an orchestration BEAT — a curated milestone shown in the ③ timeline (a supervisor decision, a map/planner node's dispatch, …). Non-beats fold into "background steps". Generic across run shapes.
  beat: boolean;
  /// For a beat step, its semantic verb (plan / spawn / retry / ask_human / merge / resolve / stop / dispatch) — the semantic pill. Null for a non-beat step.
  verb?: string | null;
  title: string;
  detail?: string | null;
  rationale?: string | null;
  /// The operator's answer on an ASK_HUMAN step (approve, or the requested change) — a structured field the FE renders
  /// as its own "└ answer" line rather than parsing it out of the joined question detail. Null unless answered.
  answer?: string | null;
  /// The structured facts of a MODEL_CALL step (purpose · model · tokens · latency · cost · status) — the expanded model
  /// fold renders these as a legible row. Null on every non-model-call step.
  modelCall?: JournalModelCall | null;
  tone: JournalTone;
  milestone: boolean;
  agents: JournalAgentCard[];
  deferred: JournalDeferredSubtask[];
  /// The subtasks this PLAN step authored — rendered inline under "planned the work". Empty for a non-plan step.
  plan: JournalSubtask[];
  agentRunId?: string | null;
  nodeId?: string | null;
}

/// One attempt of a turn — a rerun / replay of the same user message. Mirrors backend `JournalAttempt`.
export interface JournalAttempt {
  attemptNumber: number;
  runId: string;
  status: WorkflowRunStatus;
  at: string;
  sourceType: string;
  rerunFromNodeId?: string | null;
  isLatest: boolean;
  focused: boolean;
  error?: string | null;
}

/// One turn of the journal — a user message + the AI's reply as chronological steps. Mirrors backend `JournalTurn`.
export interface JournalTurn {
  turnIndex: number;
  turnRunId: string;
  runId: string;
  status: WorkflowRunStatus;
  userMessage?: string | null;
  summary?: string | null;
  at?: string | null;
  durationMs?: number | null;
  focused: boolean;
  steps: JournalStep[];
  stepCount: number;
  attempts: JournalAttempt[];
}

/// A session as a chronological work journal. Mirrors backend `JournalView`.
export interface JournalView {
  sessionId: string;
  title: string;
  kind: WorkSessionKind;
  status: WorkSessionStatus;
  cursor: string;
  anchorTurnIndex?: number | null;
  turns: JournalTurn[];
}

// ─── Client (mirrors src/api/workflows.ts — fetchJson, auto JWT + X-Team-Id) ───

export const sessionsApi = {
  /// The team's sessions, most-recently-active first, keyset-paginated.
  listTeamSessions: (cursor?: string, limit = 30) =>
    fetchJson<SessionPage>(`/api/sessions?limit=${limit}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`),

  /// One thread as a conversation (turns + nested attempts).
  getSessionDetail: (sessionId: string) => fetchJson<SessionDetail>(`/api/sessions/${sessionId}`),

  /// Rename a session's thread title — 204 on success, 404 when foreign / missing. The backend sanitises + truncates.
  renameSession: (sessionId: string, title: string) =>
    fetchJson<void>(`/api/sessions/${sessionId}`, { method: "PATCH", body: JSON.stringify({ title }) }),

  /// The Session Room for the session a run belongs to, focused on that run's turn — null when the run has no session (404).
  getRunRoom: async (runId: string): Promise<RoomView | null> => {
    try {
      return await fetchJson<RoomView>(`/api/sessions/by-run/${runId}/room`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// The Session Room for a session, focused on `focusRunId`'s turn when given (else the latest turn).
  getSessionRoom: (sessionId: string, focusRunId?: string) =>
    fetchJson<RoomView>(`/api/sessions/${sessionId}/room${focusRunId ? `?focusRunId=${encodeURIComponent(focusRunId)}` : ""}`),

  /// The Session Journal for the session a run belongs to, focused on that run's turn — null when the run has no session (404).
  getRunJournal: async (runId: string): Promise<JournalView | null> => {
    try {
      return await fetchJson<JournalView>(`/api/sessions/by-run/${runId}/journal`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// The Session Journal for a session, focused on `focusRunId`'s turn when given (else the latest turn).
  getSessionJournal: (sessionId: string, focusRunId?: string) =>
    fetchJson<JournalView>(`/api/sessions/${sessionId}/journal${focusRunId ? `?focusRunId=${encodeURIComponent(focusRunId)}` : ""}`),

  /// A generic preview of one file a run's turn produced, keyed by repo-relative path. Pass `agentRunId` to scope to one
  /// agent's version (per-agent attribution). Null when the run is foreign / missing (404).
  getRoomFile: async (runId: string, path: string, agentRunId?: string): Promise<RoomFilePreview | null> => {
    try {
      const scope = agentRunId ? `&agentRunId=${encodeURIComponent(agentRunId)}` : "";
      return await fetchJson<RoomFilePreview>(`/api/sessions/by-run/${runId}/room/file?path=${encodeURIComponent(path)}${scope}`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// Answer the run's pending plan-confirmation card (S3 gate): approve releases execution; a non-approve answer
  /// carries the operator's revision feedback (the supervisor authors a revised plan version). Null when nothing is
  /// pending (already answered / not parked / foreign run — 404).
  confirmRunPlan: async (runId: string, body: { approve: boolean; feedback?: string }): Promise<WorkPlanConfirmationOutcome | null> => {
    try {
      return await fetchJson<WorkPlanConfirmationOutcome>(`/api/workflows/runs/${runId}/plan/confirm`, { method: "POST", body: JSON.stringify(body) });
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },

  /// The thread a run belongs to, anchored at that run's turn — null when the run has no session (404). For the
  /// run-detail → session entry: any run (a turn or a rerun attempt) resolves to the same thread.
  getRunSession: async (runId: string): Promise<SessionDetail | null> => {
    try {
      return await fetchJson<SessionDetail>(`/api/workflows/runs/${runId}/session`);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  },
};
