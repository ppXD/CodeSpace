import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import type { RunListFilterInput, RunPhasesResponse, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";
import { CockpitBoard } from "@/components/workflows/CockpitBoard";
import { RunViewerDialog } from "@/components/workflows/RunViewerDialog";
import { CockpitCards, type CockpitMetrics } from "@/components/workflows/CockpitCards";
import { RunFilterBar } from "@/components/workflows/RunFilterBar";
import { summarizeDecisions, summarizeToday, type CockpitFilter } from "@/components/workflows/cockpit";
import { summarizeRunState } from "@/components/workflows/runPhases";
import { useLiveRunsPhases, usePendingDecisions, useTeamRuns, useTeamRunSummary, useTeamRunsHistory } from "@/hooks/use-workflows";

/** History = terminal runs only; the live + attention runs live in the pinned zones above, so History never duplicates them. */
const TERMINAL_STATUSES: WorkflowRunStatus[] = ["Success", "Failure", "Cancelled"];
const HISTORY_PAGE_SIZE = 20;
/** The Live + Needs-attention zones are normally a handful; fetch up to this for the zone preview + its "View all". */
const LIVE_LIMIT = 50;
const ATTENTION_LIMIT = 50;

/**
 * The Runs cockpit — the team's run control center, not a history table. Four status cards answer "is anything on
 * fire / is the system working" at a glance and arm a filter; the board below is where you act (Needs attention),
 * watch (Live), or scan (Recent). Each run opens the same Run Room. Polled while anything is still live.
 */
/** The cockpit's status/time lens as a URL value (the non-null CockpitFilter members). */
type CockpitLens = Exclude<CockpitFilter, null>;
const COCKPIT_LENSES: readonly CockpitLens[] = ["attention", "live", "failed", "today"];

/** URL search contract for the cockpit. The agent scope seeds the bar (Agents roster "View runs" drill-down); the
 *  lens + history page are deep-linkable so a cockpit VIEW is shareable / bookmarkable. Default lens (none) and page
 *  (1) are omitted for a clean URL. The rest of the bar scope stays local state — extra keys can join here later. */
type RunsSearch = { agentDefinitionIds?: string[]; lens?: CockpitLens; historyPage?: number };

/** Parse + whitelist the cockpit URL search — invalid lenses and the default page (1) drop for a clean URL. Exported for unit test. */
export function validateRunsSearch(search: Record<string, unknown>): RunsSearch {
  const raw = search.agentDefinitionIds;
  const ids = Array.isArray(raw)
    ? raw.filter((x): x is string => typeof x === "string" && x.length > 0)
    : typeof raw === "string" && raw ? [raw] : [];
  const lens = COCKPIT_LENSES.find((l) => l === search.lens);
  const page = typeof search.historyPage === "number" && search.historyPage > 1 ? Math.floor(search.historyPage) : undefined;
  return {
    ...(ids.length ? { agentDefinitionIds: ids } : {}),
    ...(lens ? { lens } : {}),
    ...(page ? { historyPage: page } : {}),
  };
}

export const Route = createFileRoute("/_app/teams/$teamSlug/runs/")({
  component: TeamRunsPage,
  validateSearch: validateRunsSearch,
});

function TeamRunsPage() {
  const { teamSlug } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate();
  // The bar's server-side scope filter (which kind / repo / project / actor / agent); the cards below are the
  // orthogonal client-side status/time lens over whatever this scope returns. Seeded once from the URL's agent scope
  // so the Agents roster's "View runs" lands pre-filtered to that persona; the bar owns it thereafter.
  const [scope, setScope] = useState<RunListFilterInput>(() =>
    search.agentDefinitionIds?.length ? { agentDefinitionIds: search.agentDefinitionIds } : {});

  // Lens + history page are URL-driven so a cockpit view is shareable / bookmarkable and Back/Forward work.
  const filter: CockpitFilter = search.lens ?? null;
  const historyPage = search.historyPage ?? 1;
  const patchSearch = (patch: Partial<RunsSearch>) =>
    navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug }, search: (prev) => ({ ...prev, ...patch }) });
  const setHistoryPage = (p: number) => patchSearch({ historyPage: p <= 1 ? undefined : p });
  // A session-less run has no Session room to navigate to, so it opens its raw detail in a modal OVER this list (the
  // list stays mounted behind it, closing returns you to the exact same scroll/page). null = no run open.
  const [modalRunId, setModalRunId] = useState<string | null>(null);

  // A slow clock so the relative ages ("oldest 14m", "waiting 2d") and today's window stay fresh without churning.
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000);
    return () => clearInterval(t);
  }, []);

  const hasScope = Object.values(scope).some((v) => (Array.isArray(v) ? v.length > 0 : v != null));
  // The caller's local start-of-day — stable within a day, so the summary's cache key is stable.
  const day = new Date(nowMs);
  const todayStartIso = new Date(day.getFullYear(), day.getMonth(), day.getDate()).toISOString();

  // ── Data: the cards read TRUE scoped counts; each zone fetches its OWN set so the number and the list always agree. ──
  const runs = useTeamRuns(scope);   // newest-50 — powers the failed/today armed views + the today sparkline
  const summary = useTeamRunSummary(scope, todayStartIso);
  const decisions = usePendingDecisions();
  // LIVE = actively progressing, no human needed: running/queued PLUS auto-resuming suspends — a fan-out parked on its
  // agent runs is WORKING, not waiting on you. A suspend that needs a person (Approval/Action) is excluded (it's attention).
  const liveRuns = useTeamRuns({ ...scope, statuses: ["Pending", "Enqueued", "Running", "Suspended"], needsAttention: false }, LIVE_LIMIT);
  // NEEDS-ATTENTION = human-ACTIONABLE suspends only (no pending decision), its OWN set so the count == the listed rows.
  const attentionRuns = useTeamRuns({ ...scope, statuses: ["Suspended"], needsAttention: true, hasPendingDecision: false }, ATTENTION_LIMIT);
  // History is its own numbered page of TERMINAL runs, only fetched on the default board (an armed card hides it).
  const history = useTeamRunsHistory({ ...scope, statuses: TERMINAL_STATUSES }, historyPage, HISTORY_PAGE_SIZE, filter === null);

  const runList = runs.data ?? [];
  const decisionList = decisions.data ?? [];
  const liveList = liveRuns.data ?? [];
  const attentionList = attentionRuns.data ?? [];

  // The live runs' phases, batched — powers the Live zone's state sentences + the "agents active" tally.
  const liveIds = liveList.map((r) => r.id);
  const livePhases = useLiveRunsPhases(liveIds);

  // ── Render-time derivations (no hooks below this line). ──
  const changeScope = (next: RunListFilterInput) => { setScope(next); setHistoryPage(1); };
  const toggleFilter = (f: CockpitFilter) => patchSearch({ lens: filter === f ? undefined : (f ?? undefined) });

  // A shrunk History result set can strand the page past the end (no rows, no pager) — snap it back once known.
  const historyPages = Math.max(1, Math.ceil((history.data?.totalCount ?? 0) / HISTORY_PAGE_SIZE));
  // Clamp an out-of-range page (e.g. the result set shrank under a filter) — in an effect, since the setter now
  // navigates. Guarded by the > check so it can't loop.
  useEffect(() => {
    if (history.data && historyPage > historyPages) setHistoryPage(historyPages);
  }, [history.data, historyPage, historyPages]);   // eslint-disable-line react-hooks/exhaustive-deps

  const phasesByRun = new Map<string, RunPhasesResponse>();
  liveIds.forEach((id, i) => { const d = livePhases[i]?.data; if (d) phasesByRun.set(id, d); });
  const agentsActive = [...phasesByRun.values()].reduce((n, p) => n + summarizeRunState(p.runStatus, p.phases).activeAgents, 0);

  const s = summary.data;
  const metrics: CockpitMetrics = {
    decisions: summarizeDecisions(decisionList, nowMs),
    suspendedReview: s?.suspendedNeedingReview ?? 0,
    liveCount: s?.live ?? 0,
    agentsActive,
    failed: s?.failed ?? 0,
    suspended: s?.suspended ?? 0,
    today: { count: s?.today ?? 0, hourly: summarizeToday(runList, nowMs).hourly },
  };
  // Open the LATEST attempt — the row IS the lineage's latest (WorkflowRunSummary.id), so the room lands on the most
  // recent run with the attempt switcher available to step back to earlier attempts (opening the root instead stranded
  // the reader on the oldest attempt). A session-backed run opens the full-page Session room; a session-less run (legacy)
  // opens its raw detail in a modal over this list. hasSession is undefined against an older backend → treat as "has session".
  const openRun = (run: WorkflowRunSummary) => {
    if (run.hasSession === false) setModalRunId(run.id);
    else navigate({ to: "/teams/$teamSlug/runs/$runNumber", params: { teamSlug, runNumber: String(run.runNumber) } });
  };

  const errorBanner = runs.error instanceof ApiError ? (
    <div className="cn-banner cn-banner-err">
      <div className="cn-banner-h">Couldn't load runs</div>
      <div className="cn-banner-p">{runs.error.message}</div>
    </div>
  ) : null;

  return (
    <>
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Runs</span></div>
        <div className="ct-title-row">
          <h1 className="ct-title">Runs</h1>
        </div>
      </div>

      <div className="ct-body">
        {runs.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

        {/* Whenever there are runs OR a scope is set, render the cockpit WITH the bar — even on an errored refetch,
            so the user can always clear/adjust the filter rather than being stranded on a bare error banner. */}
        {!runs.isLoading && (runList.length > 0 || hasScope) && (
          <div className="cockpit">
            <RunFilterBar filter={scope} onChange={changeScope} />

            {errorBanner}

            {runList.length > 0 ? (
              <>
                <CockpitCards metrics={metrics} filter={filter} onFilter={toggleFilter} />
                <CockpitBoard
                  runs={runList}
                  decisions={decisionList}
                  live={liveList}
                  attention={{ runs: attentionList, total: s?.suspendedNeedingReview ?? 0 }}
                  phasesByRun={phasesByRun}
                  filter={filter}
                  history={{ items: history.data?.items ?? [], total: history.data?.totalCount ?? 0, page: historyPage, pageSize: HISTORY_PAGE_SIZE, isLoading: history.isLoading, onPage: setHistoryPage }}
                  nowMs={nowMs}
                  onOpen={openRun}
                  onFilter={toggleFilter}
                />
              </>
            ) : !runs.error ? (
              <div className="ct-empty">
                <div className="ct-empty-h">No runs match these filters</div>
                <div className="ct-empty-p">Adjust or clear the filters above to see more runs.</div>
              </div>
            ) : null}
          </div>
        )}

        {/* Genuinely empty (no runs, no scope): the error banner if the load failed, otherwise the first-run nudge. */}
        {!runs.isLoading && runList.length === 0 && !hasScope && (
          runs.error instanceof ApiError ? (
            <div style={{ margin: 16 }}>{errorBanner}</div>
          ) : (
            <div className="ct-empty">
              <div className="ct-empty-h">No runs yet</div>
              <div className="ct-empty-p">Launch a task or run a workflow and it'll show up here.</div>
            </div>
          )
        )}
      </div>
    </section>

    {/* A session-less run's raw detail, over the list. The list stays mounted behind the fixed-position modal, so
        closing returns you to the exact same scroll/page (no navigation, no reset to page 1). */}
    {modalRunId && <RunViewerDialog runId={modalRunId} onClose={() => setModalRunId(null)} defaultView="trace" />}
    </>
  );
}
