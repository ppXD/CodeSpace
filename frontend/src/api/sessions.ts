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
  tokens?: number | null;
  costUsd?: number | null;
  filesChanged?: number | null;
  /// Tool calls the agent made — for the card meta "3 files · 6 tool calls · 41s". Null when unknown.
  toolCount?: number | null;
  /// Wall-clock for the agent — final once terminal, else live elapsed. Null before it starts.
  durationMs?: number | null;
  /// The agent's own one-line result takeaway (what it concluded) — shown on the card before any raw log.
  summary?: string | null;
  latestLine?: string | null;
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
export interface NarrativeStepBlock extends RoomBlockBase { type: "narrative_step"; text: string; tone?: NarrativeTone; at?: string | null; }
export interface AgentGroupBlock extends RoomBlockBase { type: "agent_group"; title: string; agents: RoomAgentCard[]; }
/// A collapsible stat row — one generic block for subtasks / files / tools / reasoning (the projector fills kind/label/detail/items).
export interface StatBlock extends RoomBlockBase { type: "stat"; kind: string; label: string; detail?: string | null; items?: StatItem[] | null; }
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
}

export type RoomBlock =
  | UserMessageBlock
  | AssistantTurnBlock
  | ExecutionMapBlock
  | NarrativeStepBlock
  | AgentGroupBlock
  | StatBlock
  | DeliveryBlock
  | DecisionBlock
  | DiagnosticBlock
  | FinalAnswerBlock
  | LiveActivityBlock;

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
