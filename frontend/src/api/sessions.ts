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

// ─── Client (mirrors src/api/workflows.ts — fetchJson, auto JWT + X-Team-Id) ───

export const sessionsApi = {
  /// The team's sessions, most-recently-active first, keyset-paginated.
  listTeamSessions: (cursor?: string, limit = 30) =>
    fetchJson<SessionPage>(`/api/sessions?limit=${limit}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`),

  /// One thread as a conversation (turns + nested attempts).
  getSessionDetail: (sessionId: string) => fetchJson<SessionDetail>(`/api/sessions/${sessionId}`),

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
