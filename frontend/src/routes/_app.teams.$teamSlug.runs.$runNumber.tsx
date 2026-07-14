import { useEffect } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import { RunDetailView, type RunView } from "@/components/workflows/RunDetailView";
import type { AssistantTurnBlock, RoomBlock } from "@/api/sessions";
import { useWorkflowRun } from "@/hooks/use-workflows";
import { useRunJournal, useRunRoom } from "@/hooks/use-sessions";
import { SessionRoomView } from "@/components/sessions/SessionRoomView";
import type { PaneView } from "@/components/sessions/RoomRunPane";

/**
 * The run-detail URL search. `?trace=` / `?view=` are the LEGACY run-trace-modal deep-links (the modal was
 * decommissioned in D4); they're still parsed so an old link can be one-time rewritten to the companion pane (see
 * {@link rewriteTraceDeepLink}) and never 404. `?pane={canvas|changes|trace}` = the in-Room run companion pane on the
 * given mini-tab (D1 opened only `canvas`; D5 widened it to the three mini-tabs). `&turn=N` = PINNED to turn N (D2);
 * `?pane` with no `turn` = FOLLOW the latest turn. `&node={id}` = D3 canvas focus — center + ring that node on the 畫布
 * tab (set by a journal jump affordance).
 */
type RunDetailSearch = { trace?: string; view?: RunView; pane?: PaneView; turn?: number; node?: string };
const RUN_VIEWS: readonly RunView[] = ["activity", "canvas", "changes", "trace"];
const PANE_VIEWS: readonly PaneView[] = ["canvas", "changes", "trace"];

/** Parse + whitelist the run-detail URL search — an unknown view and an empty trace drop for a clean URL. Exported for unit test. */
export function validateRunDetailSearch(search: Record<string, unknown>): RunDetailSearch {
  const view = RUN_VIEWS.find((v) => v === search.view);
  const trace = typeof search.trace === "string" && search.trace ? search.trace : undefined;
  // The companion pane's presence is `pane` (the active mini-tab). `turn` is the D2 PIN — kept only alongside a valid
  // pane, dropped when absent/invalid (that's FOLLOW mode). A lone `?turn` with no `?pane` drops entirely, so the URL
  // never carries a dangling pin without an open pane.
  const pane = PANE_VIEWS.find((v) => v === search.pane);
  const turnNum = typeof search.turn === "number" ? search.turn : typeof search.turn === "string" ? Number(search.turn) : NaN;
  const turn = pane && Number.isInteger(turnNum) && turnNum > 0 ? turnNum : undefined;
  // `node` is the D3 canvas focus — a node id, kept only alongside a valid pane (like `turn`); dropped when absent/empty
  // or when there's no open pane to focus within.
  const node = pane && typeof search.node === "string" && search.node ? search.node : undefined;
  return { ...(trace ? { trace } : {}), ...(view ? { view } : {}), ...(pane ? { pane, ...(turn ? { turn } : {}), ...(node ? { node } : {}) } : {}) };
}

/** The outcome of rewriting a legacy run-trace-modal deep-link — see {@link rewriteTraceDeepLink}. */
export type TraceRewrite =
  | { kind: "none" }                                    // no legacy ?trace — nothing to do
  | { kind: "pending" }                                 // this run + a pane view, but the room hasn't resolved the turn yet
  | { kind: "subrun"; runId: string }                   // ?trace names another run — go to its own page
  | { kind: "clear" }                                   // this run + activity — drop the legacy params, keep just the journal
  | { kind: "pane"; pane: PaneView; turn?: number };    // this run — open the matching mini-tab (pinned to its turn if known)

/**
 * The one-time rewrite of a LEGACY `?trace=`/`?view=` run-trace-modal deep-link (the modal was decommissioned in D4)
 * to the companion pane. A `?trace` naming THIS run maps its inner view to a pane mini-tab — activity → no pane (the
 * journal already IS the activity), canvas/changes/trace → the matching tab pinned to this run's turn; a missing
 * `?view` meant the modal's default tab (trace). A `?trace` naming a DIFFERENT (sub / sibling) run has its own page, so
 * we navigate there. Pure + exported so the redirect matrix is unit-testable without rendering the heavy Room.
 */
export function rewriteTraceDeepLink(trace: string | undefined, view: RunView | undefined, runId: string, blocks: RoomBlock[] | undefined): TraceRewrite {
  if (!trace) return { kind: "none" };
  if (trace !== runId) return { kind: "subrun", runId: trace };

  const pane: PaneView | null = view === "canvas" ? "canvas" : view === "changes" ? "changes" : view === "activity" ? null : "trace";
  if (!pane) return { kind: "clear" };

  // The pinned turn is this run's turn — found by its runId in the room blocks. Until the room resolves, hold (pending)
  // rather than pinning blind; a run with no matching turn (an old attempt) falls back to follow mode (turn omitted).
  if (!blocks) return { kind: "pending" };
  const turn = blocks.find((b): b is AssistantTurnBlock => b.type === "assistant_turn" && b.runId === runId)?.turnIndex;
  return { kind: "pane", pane, ...(turn != null ? { turn } : {}) };
}

/**
 * The canonical run-detail page. A run is run-neutral (manual, scheduled, webhook, replay, task, child), so it lives
 * at the team level under /runs. Every run belongs to a work session, so the page IS the Session — the backend-authored
 * work transcript: the Room frame (header · execution map ① · plan checklist ② · result card ⑥) with the Journal's
 * chronological steps ③ as its middle. The raw run detail (the graph, trace, node JSON, decisions) now lives in the
 * in-Room companion PANE (summoned per turn), not the removed run-trace modal; legacy `?trace`/`?view` deep-links are
 * one-time rewritten to it. A legacy/session-less run (only pre-release data, no Session) renders the shared
 * {@link RunDetailView} full-page on the app shell.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/runs/$runNumber")({
  component: RunDetailPage,
  validateSearch: validateRunDetailSearch,
});

// The URL segment is a ref — the canonical team-scoped run number, or a legacy GUID. Resolve it to the
// run (getting its real id + number) before the room/journal fetches, which are all GUID-keyed. Remount
// per resolved id so the modal state resets cleanly on navigation.
function RunDetailPage() {
  const { teamSlug, runNumber } = Route.useParams();
  const navigate = useNavigate();
  const run = useWorkflowRun(runNumber);

  // Canonicalise a legacy-GUID URL to the number URL.
  useEffect(() => {
    const resolved = run.data;
    if (resolved && String(resolved.runNumber) !== runNumber) {
      navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber: String(resolved.runNumber) }, replace: true });
    }
  }, [run.data, runNumber, teamSlug, navigate]);

  if (run.isLoading) return <div className="run-outline-empty" style={{ padding: 48 }}>Loading…</div>;

  if (run.error instanceof ApiError || !run.data) {
    return (
      <div className="run-outline-empty" style={{ padding: 48 }}>
        Run not found.
        <a className="btn" style={{ marginLeft: 8 }} onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Back to runs</a>
      </div>
    );
  }

  return <RunDetail key={run.data.id} teamSlug={teamSlug} runNumber={String(run.data.runNumber)} runId={run.data.id} />;
}

// The Session — the Room frame with the Journal's chronological steps ③ as its middle. The strangler rebuild is complete:
// the Journal is now the only view (the old room/journal toggle is gone). The room fetch drives the page frame + its
// retained rich/live blocks; the journal supplies the ③ transcript (undefined until it loads — the frame still renders).
function RunDetail({ teamSlug, runNumber, runId }: { teamSlug: string; runNumber: string; runId: string }) {
  const navigate = useNavigate();
  const search = Route.useSearch();
  const room = useRunRoom(runId);
  const journal = useRunJournal(runId);

  const patch = (p: Partial<RunDetailSearch>) =>
    navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber }, search: (prev) => ({ ...prev, ...p }) });
  // ?view= drives the session-less run detail's tab (the modal's default was "trace", omitted from the URL).
  const onViewChange = (v: RunView) => patch({ view: v === "trace" ? undefined : v });
  // The companion pane deep-link: FOLLOW binds ?pane={view} (no turn); PINNED binds ?pane={view}&turn=N; a journal jump
  // adds &node={id} (D3 canvas focus); switching a mini-tab updates ?pane in place; closing clears all three. A null view
  // means the pane closed.
  const onPaneChange = (view: PaneView | null, turn: number | null, node: string | null) => patch(view == null ? { pane: undefined, turn: undefined, node: undefined } : { pane: view, turn: turn ?? undefined, node: node ?? undefined });

  // One-time rewrite of a LEGACY ?trace=/?view= run-trace-modal deep-link (the modal was decommissioned in D4) → the
  // companion pane, so an old link never 404s. `replace` so Back skips the dead URL. A sub-run navigates to its own page;
  // this run's activity drops to the journal; a pane view opens the matching mini-tab (pinned once the room resolves its turn).
  useEffect(() => {
    const rw = rewriteTraceDeepLink(search.trace, search.view, runId, room.data?.blocks);
    if (rw.kind === "none" || rw.kind === "pending") return;
    if (rw.kind === "subrun") { navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber: rw.runId }, replace: true }); return; }
    if (rw.kind === "clear") { navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber }, search: (prev) => ({ ...prev, trace: undefined, view: undefined }), replace: true }); return; }
    navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber }, search: (prev) => ({ ...prev, trace: undefined, view: undefined, pane: rw.pane, turn: rw.turn }), replace: true });
  }, [search.trace, search.view, runId, teamSlug, runNumber, room.data, navigate]);

  if (room.data) {
    return (
      <SessionRoomView teamSlug={teamSlug} room={room.data} journal={journal.data ?? undefined} initialPaneTurn={search.turn ?? null} initialPaneView={search.pane} initialPaneNode={search.node ?? null} onPaneChange={onPaneChange} />
    );
  }

  // Still resolving whether this run has a session.
  if (room.isLoading) return <div className="run-outline-empty" style={{ padding: 48 }}>Loading…</div>;

  // The room fetch failed. getRunRoom maps ONLY 404 → null (genuinely session-less); a non-404 error (transient 500,
  // expired-token 401, network) re-throws. Don't mistake that for "no session" and strand the operator — a session-backed
  // run would then never show its Session. Offer a retry instead.
  if (room.isError) return (
    <div className="run-outline-empty" style={{ padding: 48 }}>
      Couldn't load this run.
      <button type="button" className="btn" style={{ marginLeft: 8 }} onClick={() => void room.refetch()}>Retry</button>
    </div>
  );

  // Session-less (legacy / pre-release) run — the fetch succeeded with no session (404 → null). There's no Session to host
  // the transcript, so render the shared run detail (activity / canvas / changes / trace tabs) full-page — the same body
  // the companion pane and the editor use, minus the removed modal chrome. ?view= deep-links its tab; a sub-workflow drill
  // opens that child run's own page.
  return (
    <div style={{ maxWidth: 1120, margin: "0 auto", padding: "24px 22px" }}>
      <RunDetailView runId={runId} defaultView={search.view ?? "trace"} view={search.view} onViewChange={onViewChange} onOpenRun={(rid) => navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber: rid } })} />
    </div>
  );
}
