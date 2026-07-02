import { useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { RunDetailView } from "@/components/workflows/RunDetailView";
import { RunViewerDialog } from "@/components/workflows/RunViewerDialog";
import { useRunRoom } from "@/hooks/use-sessions";
import { SessionRoomView } from "@/components/sessions/SessionRoomView";

/**
 * The canonical run-detail page. A run is run-neutral (manual, scheduled, webhook, replay, task, child), so it lives
 * at the team level under /runs. Every run belongs to a work session, so the page IS the Session room — the backend-
 * authored transcript (RoomView). The raw run detail (the graph, trace, node JSON, decisions) opens IN A MODAL over
 * the room — the same <see cref="RunViewerDialog"/> the workflow editor uses — so "View trace" / "Run details" never
 * navigates away from the conversation. There is no longer a separate classic full-page Run Detail.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/runs/$runId")({
  component: RunDetailPage,
});

// Remount per URL run id so the modal state resets cleanly on navigation (a path-param change doesn't remount by default).
function RunDetailPage() {
  const { teamSlug, runId } = Route.useParams();
  return <RunDetailRoom key={runId} teamSlug={teamSlug} runId={runId} />;
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

  // Loading, or a session-less run (only pre-release / legacy data — every new run has a session). Fall back to the
  // bare run detail so the run stays viewable, without the removed classic three-column chrome.
  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 16 }}>
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Runs</a>
          <span className="sep">/</span>
          <span className="cur">Run {runId.slice(0, 8)}</span>
        </div>
      </div>
      <div className="ct-body">
        {room.isLoading
          ? <div className="run-outline-empty">Loading…</div>
          : <div className="run-panel"><RunDetailView runId={runId} onOpenRun={(childRunId) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId: childRunId } })} /></div>}
      </div>
    </section>
  );
}
