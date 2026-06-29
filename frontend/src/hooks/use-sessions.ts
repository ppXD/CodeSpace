import { keepPreviousData, useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { sessionsApi, type RoomView, type SessionDetail } from "@/api/sessions";
import { isRunActive } from "@/hooks/use-workflows";

/// True while ANY turn's run is still progressing — gates the live-poll on the detail/by-run views.
function hasActiveTurn(detail: SessionDetail | null | undefined): boolean {
  return !!detail?.turns.some((t) => isRunActive(t.runStatus));
}

/// True while any turn in the room is still progressing — gates the room's live-poll.
function hasActiveRoomTurn(room: RoomView | null | undefined): boolean {
  return !!room?.blocks.some((b) => b.type === "assistant_turn" && isRunActive(b.status));
}

/// The team's sessions index — keyset-paginated for an infinite-scroll sidebar, polled while open so live threads
/// surface their latest activity. Mirrors the runs list's 4s cadence + keepPreviousData (no flicker on page turns).
export function useTeamSessions(limit = 30) {
  return useInfiniteQuery({
    queryKey: ["team-sessions", limit],
    queryFn: ({ pageParam }) => sessionsApi.listTeamSessions(pageParam, limit),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    refetchInterval: 4000,
    placeholderData: keepPreviousData,
  });
}

/// One thread as a conversation. Polls every 2s while a turn is still running (mirrors useWorkflowRun), then stops.
export function useSessionDetail(sessionId: string | null | undefined) {
  return useQuery({
    queryKey: ["session", sessionId],
    queryFn: () => sessionsApi.getSessionDetail(sessionId!),
    enabled: sessionId != null,
    refetchInterval: (q) => (hasActiveTurn(q.state.data) ? 2000 : false),
  });
}

/// The thread a run belongs to (null when the run has no session). Drives the run-detail → session entry; same 2s
/// live cadence as the detail view. The query is enabled even when the result may be null — a null means "no session".
export function useRunSession(runId: string | null | undefined) {
  return useQuery({
    queryKey: ["run-session", runId],
    queryFn: () => sessionsApi.getRunSession(runId!),
    enabled: runId != null,
    refetchInterval: (q) => (hasActiveTurn(q.state.data) ? 2000 : false),
  });
}

/// The backend-authored Session Room for the session a run belongs to (null when the run has no session). Polls every
/// 2s while any turn is still running, then stops. This is the run-detail → room entry: the frontend renders the
/// returned blocks by type and owns no copy / order / status.
export function useRunRoom(runId: string | null | undefined) {
  return useQuery({
    queryKey: ["run-room", runId],
    queryFn: () => sessionsApi.getRunRoom(runId!),
    enabled: runId != null,
    refetchInterval: (q) => (hasActiveRoomTurn(q.state.data) ? 2000 : false),
  });
}
