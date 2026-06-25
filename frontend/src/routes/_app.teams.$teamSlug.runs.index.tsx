import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import type { RunListFilterInput, RunPhasesResponse, WorkflowRunStatus } from "@/api/workflows";
import { CockpitBoard } from "@/components/workflows/CockpitBoard";
import { CockpitCards, type CockpitMetrics } from "@/components/workflows/CockpitCards";
import { RunFilterBar } from "@/components/workflows/RunFilterBar";
import { summarizeDecisions, summarizeToday, type CockpitFilter } from "@/components/workflows/cockpit";
import { summarizeRunState } from "@/components/workflows/runPhases";
import { bucketRuns } from "@/components/workflows/runsIndex";
import { useLiveRunsPhases, usePendingDecisions, useTeamRuns, useTeamRunSummary, useTeamRunsHistory } from "@/hooks/use-workflows";

/** History = terminal runs only; the live + suspended runs live in the pinned zones above, so History never duplicates them. */
const TERMINAL_STATUSES: WorkflowRunStatus[] = ["Success", "Failure", "Cancelled"];
const HISTORY_PAGE_SIZE = 20;
/** Suspended-needing-review (the Needs-attention zone) is normally a handful; fetch up to this for the zone + its "View all". */
const ATTENTION_LIMIT = 50;

/**
 * The Runs cockpit — the team's run control center, not a history table. Four status cards answer "is anything on
 * fire / is the system working" at a glance and arm a filter; the board below is where you act (Needs attention),
 * watch (Live), or scan (Recent). Each run opens the same Run Room. Polled while anything is still live.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/runs/")({
  component: TeamRunsPage,
});

function TeamRunsPage() {
  const { teamSlug } = Route.useParams();
  const navigate = useNavigate();
  // The bar's server-side scope filter (which kind / repo / project / actor / agent); the cards below are the
  // orthogonal client-side status/time lens over whatever this scope returns.
  const [scope, setScope] = useState<RunListFilterInput>({});
  const [filter, setFilter] = useState<CockpitFilter>(null);
  const [historyPage, setHistoryPage] = useState(1);

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
  const runs = useTeamRuns(scope);   // newest-50 — powers the Live zone + the failed/today armed views + the today sparkline
  const summary = useTeamRunSummary(scope, todayStartIso);
  const decisions = usePendingDecisions();
  // The Needs-attention zone fetches its OWN runs (suspended, no pending decision) so the card can't say "1" while the
  // zone shows nothing for a run outside the newest-50 — the number, the preview, and "View all" are one set.
  const attentionRuns = useTeamRuns({ ...scope, statuses: ["Suspended"], hasPendingDecision: false }, ATTENTION_LIMIT);
  // History is its own numbered page of TERMINAL runs, only fetched on the default board (an armed card hides it).
  const history = useTeamRunsHistory({ ...scope, statuses: TERMINAL_STATUSES }, historyPage, HISTORY_PAGE_SIZE, filter === null);

  const runList = runs.data ?? [];
  const decisionList = decisions.data ?? [];
  const attentionList = attentionRuns.data ?? [];

  // The live runs' phases, batched — powers the Live zone's state sentences + the "agents active" tally.
  const liveIds = bucketRuns(runList).live.map((r) => r.id);
  const livePhases = useLiveRunsPhases(liveIds);

  // ── Render-time derivations (no hooks below this line). ──
  const changeScope = (next: RunListFilterInput) => { setScope(next); setHistoryPage(1); };
  const toggleFilter = (f: CockpitFilter) => setFilter((cur) => (cur === f ? null : f));

  // A shrunk History result set can strand the page past the end (no rows, no pager) — snap it back once known.
  const historyPages = Math.max(1, Math.ceil((history.data?.totalCount ?? 0) / HISTORY_PAGE_SIZE));
  if (history.data && historyPage > historyPages) setHistoryPage(historyPages);

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
  const openRun = (runId: string) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId } });

  // Hold the board until the summary's FIRST load too, so the cards never flash a false all-zero "All clear" / "Idle"
  // before the true counts arrive (a changed scope keeps the prior numbers via keepPreviousData, so this only gates
  // the very first paint; a summary error degrades to zeros rather than hanging).
  const cockpitLoading = runs.isLoading || (summary.isLoading && !summary.data);

  const errorBanner = runs.error instanceof ApiError ? (
    <div className="cn-banner cn-banner-err">
      <div className="cn-banner-h">Couldn't load runs</div>
      <div className="cn-banner-p">{runs.error.message}</div>
    </div>
  ) : null;

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Runs</span></div>
        <div className="ct-title-row">
          <h1 className="ct-title">Runs</h1>
        </div>
      </div>

      <div className="ct-body">
        {cockpitLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

        {/* Whenever there are runs OR a scope is set, render the cockpit WITH the bar — even on an errored refetch,
            so the user can always clear/adjust the filter rather than being stranded on a bare error banner. */}
        {!cockpitLoading && (runList.length > 0 || hasScope) && (
          <div className="cockpit">
            <RunFilterBar filter={scope} onChange={changeScope} />

            {errorBanner}

            {runList.length > 0 ? (
              <>
                <CockpitCards metrics={metrics} filter={filter} onFilter={toggleFilter} />
                <CockpitBoard
                  runs={runList}
                  decisions={decisionList}
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
        {!cockpitLoading && runList.length === 0 && !hasScope && (
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
  );
}
