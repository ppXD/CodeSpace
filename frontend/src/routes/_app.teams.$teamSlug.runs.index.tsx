import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { ApiError } from "@/api/request";
import { RunsZones } from "@/components/workflows/RunsIndexList";
import { useTeamRuns, useWorkflows } from "@/hooks/use-workflows";

/**
 * The team's Runs index — every run the team has launched, any source, in three zones read top-to-bottom:
 * Needs attention (parked on a human signal), Live (in flight), Recent (settled). Each row opens the Run Room.
 * A monitoring surface: read-only, polled while anything is still running.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/runs/")({
  component: TeamRunsPage,
});

function TeamRunsPage() {
  const { teamSlug } = Route.useParams();
  const navigate = useNavigate();
  const runs = useTeamRuns();
  const workflows = useWorkflows();

  const nameById = new Map((workflows.data ?? []).map((w) => [w.id, w.name]));
  const total = (runs.data ?? []).length;

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

        {!runs.isLoading && !runs.error && total === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No runs yet</div>
            <div className="ct-empty-p">Launch a task or run a workflow and it'll show up here.</div>
          </div>
        )}

        {!runs.isLoading && !runs.error && total > 0 && (
          <RunsZones runs={runs.data ?? []} nameById={nameById} onOpen={openRun} />
        )}
      </div>
    </section>
  );
}
