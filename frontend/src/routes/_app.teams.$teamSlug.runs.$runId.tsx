import { useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { RunViewerDialog } from "@/components/workflows/RunViewerDialog";
import { useRunJournal, useRunRoom } from "@/hooks/use-sessions";
import { SessionRoomView } from "@/components/sessions/SessionRoomView";

/**
 * The canonical run-detail page. A run is run-neutral (manual, scheduled, webhook, replay, task, child), so it lives
 * at the team level under /runs. Every run belongs to a work session, so the page IS the Session room — the backend-
 * authored transcript (RoomView). The raw run detail (the graph, trace, node JSON, decisions) opens IN A MODAL — the
 * same {@link RunViewerDialog} the workflow editor uses — so "View trace" / "Run details" never navigates away.
 *
 * A `?view=journal` toggle renders the NEW Session Journal (a chronological work transcript) alongside the room — the
 * strangler rebuild, default still the room. There is no standalone full-page run detail. A legacy/session-less run
 * (only pre-release data) opens the SAME modal on its own over the app shell.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/runs/$runId")({
  component: RunDetailPage,
  validateSearch: (raw: Record<string, unknown>): { view?: "journal" } =>
    raw.view === "journal" ? { view: "journal" } : {},
});

// Remount per URL run id so the modal state resets cleanly on navigation (a path-param change doesn't remount by default).
function RunDetailPage() {
  const { teamSlug, runId } = Route.useParams();
  return <RunDetail key={runId} teamSlug={teamSlug} runId={runId} />;
}

function RunDetail({ teamSlug, runId }: { teamSlug: string; runId: string }) {
  const { view } = Route.useSearch();
  return (
    <>
      <ViewToggle journal={view === "journal"} />
      {view === "journal" ? (
        <RunDetailJournal teamSlug={teamSlug} runId={runId} />
      ) : (
        <RunDetailRoom teamSlug={teamSlug} runId={runId} />
      )}
    </>
  );
}

// The room ⇄ journal switch — a floating pill that flips the ?view= search param (omit for the default room).
function ViewToggle({ journal }: { journal: boolean }) {
  const navigate = useNavigate();
  return (
    <div className="jr-toggle">
      <button className={!journal ? "jr-toggle-on" : ""} onClick={() => navigate({ to: ".", search: {} })}>Room</button>
      <button className={journal ? "jr-toggle-on" : ""} onClick={() => navigate({ to: ".", search: { view: "journal" } })}>Journal</button>
    </div>
  );
}

// Journal mode REUSES the Room frame: it renders the same SessionRoomView (header · execution map ① · plan checklist ② ·
// result card ⑥ · mono style) but passes the journal, so each turn's narrative blocks are replaced by the journal's
// chronological steps ③. The journal is the Room with a better middle, not a different-looking page.
function RunDetailJournal({ teamSlug, runId }: { teamSlug: string; runId: string }) {
  const navigate = useNavigate();
  const room = useRunRoom(runId);
  const journal = useRunJournal(runId);
  const [detailRunId, setDetailRunId] = useState<string | null>(null);

  if (room.data) {
    return (
      <>
        <SessionRoomView teamSlug={teamSlug} room={room.data} journal={journal.data ?? undefined} onOpenRoom={(rid) => setDetailRunId(rid ?? runId)} />
        {detailRunId && <RunViewerDialog runId={detailRunId} onClose={() => setDetailRunId(null)} defaultView="trace" />}
      </>
    );
  }

  if (room.isLoading) return <div className="run-outline-empty" style={{ padding: 48 }}>Loading…</div>;

  if (room.isError) return (
    <div className="run-outline-empty" style={{ padding: 48 }}>
      Couldn't load this run.
      <button type="button" className="btn" style={{ marginLeft: 8 }} onClick={() => void room.refetch()}>Retry</button>
    </div>
  );

  // Session-less (legacy) run — no room/journal; open the raw run detail directly.
  return <RunViewerDialog runId={runId} onClose={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })} defaultView="trace" />;
}

function RunDetailRoom({ teamSlug, runId }: { teamSlug: string; runId: string }) {
  const navigate = useNavigate();

  // The session transcript for this run's thread (getRunRoom resolves any run — a turn or an attempt — to its thread,
  // anchored at that run's turn). Every run has a session, so this drives the whole page.
  const room = useRunRoom(runId);

  // The run whose raw detail (graph / trace / nodes) is open in the modal — null when closed. "View trace" on a turn
  // opens THAT turn's run; the header "Run details" opens the anchored run.
  const [detailRunId, setDetailRunId] = useState<string | null>(null);

  if (room.data) {
    return (
      <>
        <SessionRoomView teamSlug={teamSlug} room={room.data} onOpenRoom={(rid) => setDetailRunId(rid ?? runId)} />
        {detailRunId && <RunViewerDialog runId={detailRunId} onClose={() => setDetailRunId(null)} defaultView="trace" />}
      </>
    );
  }

  // Still resolving whether this run has a session.
  if (room.isLoading) return <div className="run-outline-empty" style={{ padding: 48 }}>Loading…</div>;

  // The room fetch failed. getRunRoom maps ONLY 404 → null (genuinely session-less); a non-404 error (transient 500,
  // expired-token 401, network) re-throws. Don't mistake that for "no session" and strand the operator in the raw-trace
  // modal — a session-backed run would then never show its Room. Offer a retry instead.
  if (room.isError) return (
    <div className="run-outline-empty" style={{ padding: 48 }}>
      Couldn't load this run.
      <button type="button" className="btn" style={{ marginLeft: 8 }} onClick={() => void room.refetch()}>Retry</button>
    </div>
  );

  // Session-less (legacy / pre-release) run — the fetch succeeded with no session (404 → null). There's no Room to host
  // the transcript, so open the raw run detail directly in the SAME modal, returning to the Runs index on close.
  return <RunViewerDialog runId={runId} onClose={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })} defaultView="trace" />;
}
