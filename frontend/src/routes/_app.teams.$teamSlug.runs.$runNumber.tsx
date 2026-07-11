import { useEffect } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import { RunViewerDialog } from "@/components/workflows/RunViewerDialog";
import type { RunView } from "@/components/workflows/RunDetailView";
import { useWorkflowRun } from "@/hooks/use-workflows";
import { useRunJournal, useRunRoom } from "@/hooks/use-sessions";
import { SessionRoomView } from "@/components/sessions/SessionRoomView";

/** The raw-trace modal is deep-linkable: `?trace=` = which run's modal is open, `?view=` = its inner tab. */
type RunDetailSearch = { trace?: string; view?: RunView };
const RUN_VIEWS: readonly RunView[] = ["activity", "canvas", "changes", "trace"];

/** Parse + whitelist the run-detail URL search — an unknown view and an empty trace drop for a clean URL. Exported for unit test. */
export function validateRunDetailSearch(search: Record<string, unknown>): RunDetailSearch {
  const view = RUN_VIEWS.find((v) => v === search.view);
  const trace = typeof search.trace === "string" && search.trace ? search.trace : undefined;
  return { ...(trace ? { trace } : {}), ...(view ? { view } : {}) };
}

/**
 * The canonical run-detail page. A run is run-neutral (manual, scheduled, webhook, replay, task, child), so it lives
 * at the team level under /runs. Every run belongs to a work session, so the page IS the Session — the backend-authored
 * work transcript: the Room frame (header · execution map ① · plan checklist ② · result card ⑥) with the Journal's
 * chronological steps ③ as its middle. The raw run detail (the graph, trace, node JSON, decisions) opens IN A MODAL —
 * the same {@link RunViewerDialog} the workflow editor uses — so "View trace" / "Run details" never navigates away. A
 * legacy/session-less run (only pre-release data) opens the SAME modal on its own over the app shell.
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

  // The raw-trace modal is URL-driven: ?trace= names the open run, ?view= its inner tab — so an opened trace is
  // shareable and Back closes it. The modal's default tab is "trace", so ?view is omitted when it equals the default.
  const patch = (p: Partial<RunDetailSearch>) =>
    navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber }, search: (prev) => ({ ...prev, ...p }) });
  const onViewChange = (v: RunView) => patch({ view: v === "trace" ? undefined : v });

  if (room.data) {
    return (
      <>
        <SessionRoomView teamSlug={teamSlug} room={room.data} journal={journal.data ?? undefined} onOpenRoom={(rid) => patch({ trace: rid ?? runId })} />
        {search.trace && <RunViewerDialog runId={search.trace} view={search.view} onViewChange={onViewChange} onClose={() => patch({ trace: undefined, view: undefined })} defaultView="trace" />}
      </>
    );
  }

  // Still resolving whether this run has a session.
  if (room.isLoading) return <div className="run-outline-empty" style={{ padding: 48 }}>Loading…</div>;

  // The room fetch failed. getRunRoom maps ONLY 404 → null (genuinely session-less); a non-404 error (transient 500,
  // expired-token 401, network) re-throws. Don't mistake that for "no session" and strand the operator in the raw-trace
  // modal — a session-backed run would then never show its Session. Offer a retry instead.
  if (room.isError) return (
    <div className="run-outline-empty" style={{ padding: 48 }}>
      Couldn't load this run.
      <button type="button" className="btn" style={{ marginLeft: 8 }} onClick={() => void room.refetch()}>Retry</button>
    </div>
  );

  // Session-less (legacy / pre-release) run — the fetch succeeded with no session (404 → null). There's no Session to host
  // the transcript, so open the raw run detail directly in the SAME modal, returning to the Runs index on close.
  return <RunViewerDialog runId={runId} view={search.view} onViewChange={onViewChange} onClose={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })} defaultView="trace" />;
}
