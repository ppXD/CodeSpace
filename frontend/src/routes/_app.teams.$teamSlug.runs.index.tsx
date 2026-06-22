import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import type { RunListFilterInput, RunPhasesResponse } from "@/api/workflows";
import { CockpitBoard } from "@/components/workflows/CockpitBoard";
import { CockpitCards, type CockpitMetrics } from "@/components/workflows/CockpitCards";
import { RunFilterBar } from "@/components/workflows/RunFilterBar";
import { countRuns, summarizeDecisions, summarizeToday, suspendedNeedingReview, type CockpitFilter } from "@/components/workflows/cockpit";
import { summarizeRunState } from "@/components/workflows/runPhases";
import { bucketRuns } from "@/components/workflows/runsIndex";
import { useLiveRunsPhases, usePendingDecisions, useTeamRuns } from "@/hooks/use-workflows";

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
  const runs = useTeamRuns(scope);
  const decisions = usePendingDecisions();
  const [filter, setFilter] = useState<CockpitFilter>(null);
  const hasScope = Object.values(scope).some((v) => (Array.isArray(v) ? v.length > 0 : v != null));

  // A slow clock so the relative ages ("oldest 14m", "waiting 2d") and today's window stay fresh without churning.
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000);
    return () => clearInterval(t);
  }, []);

  const runList = runs.data ?? [];
  const decisionList = decisions.data ?? [];

  // The live runs' phases, batched — powers the Live zone's state sentences + the "agents active" tally.
  const liveIds = bucketRuns(runList).live.map((r) => r.id);
  const livePhases = useLiveRunsPhases(liveIds);
  const phasesByRun = new Map<string, RunPhasesResponse>();
  liveIds.forEach((id, i) => { const d = livePhases[i]?.data; if (d) phasesByRun.set(id, d); });
  const agentsActive = [...phasesByRun.values()].reduce((n, p) => n + summarizeRunState(p.runStatus, p.phases).activeAgents, 0);

  const counts = countRuns(runList);
  const metrics: CockpitMetrics = {
    decisions: summarizeDecisions(decisionList, nowMs),
    suspendedReview: suspendedNeedingReview(runList, decisionList).length,
    liveCount: counts.live,
    agentsActive,
    failed: counts.failed,
    suspended: counts.suspended,
    today: summarizeToday(runList, nowMs),
  };

  const toggleFilter = (f: CockpitFilter) => setFilter((cur) => (cur === f ? null : f));
  const openRun = (runId: string) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId } });

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Runs</span></div>
        <div className="ct-title-row">
          <h1 className="ct-title">Runs</h1>
        </div>
      </div>

      <div className="ct-body">
        {runs.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

        {runs.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load runs</div>
            <div className="cn-banner-p">{runs.error.message}</div>
          </div>
        )}

        {!runs.isLoading && !runs.error && (runList.length > 0 || hasScope) && (
          <div className="cockpit">
            <RunFilterBar filter={scope} onChange={setScope} />

            {runList.length === 0 ? (
              <div className="ct-empty">
                <div className="ct-empty-h">No runs match these filters</div>
                <div className="ct-empty-p">Adjust or clear the filters above to see more runs.</div>
              </div>
            ) : (
              <>
                <CockpitCards metrics={metrics} filter={filter} onFilter={toggleFilter} />
                <CockpitBoard runs={runList} decisions={decisionList} phasesByRun={phasesByRun} filter={filter} nowMs={nowMs} onOpen={openRun} />
              </>
            )}
          </div>
        )}

        {!runs.isLoading && !runs.error && runList.length === 0 && !hasScope && (
          <div className="ct-empty">
            <div className="ct-empty-h">No runs yet</div>
            <div className="ct-empty-p">Launch a task or run a workflow and it'll show up here.</div>
          </div>
        )}
      </div>
    </section>
  );
}
