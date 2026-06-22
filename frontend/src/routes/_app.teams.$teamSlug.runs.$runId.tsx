import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { DecisionInbox } from "@/components/workflows/DecisionInbox";
import { RunDetailView } from "@/components/workflows/RunDetailView";
import { RunFacts } from "@/components/workflows/RunFacts";
import { RunOutline } from "@/components/workflows/RunOutline";
import { RunStateHeader } from "@/components/workflows/RunStateHeader";
import { decisionsForRun } from "@/components/workflows/runDecisions";
import { isRunActive, usePendingDecisions, useReplayRun, useRunPhases, useWorkflowRun } from "@/hooks/use-workflows";

/**
 * The canonical run-detail page — the Run Room. A run is run-neutral (manual, scheduled, webhook, replay, task,
 * child), so it lives at the team level under /runs, NOT under any one workflow; everything that opens a run links
 * here. The run content is the shared <see cref="RunDetailView"/> (also rendered inline in the editor's run dialog);
 * this route wraps it with page chrome (breadcrumb + Re-run / Back to workflow) and the outline + decision rails.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/runs/$runId")({
  component: RunDetailPage,
});

function RunDetailPage() {
  const { teamSlug, runId } = Route.useParams();
  const navigate = useNavigate();
  // Shared with RunDetailView via the same query key (React Query dedups) — used here for the Re-run target,
  // the run-state header, and the "Back to workflow" link when the run came from an authored workflow.
  const run = useWorkflowRun(runId);
  // The run-neutral outline (the phase projection) — a separate endpoint, polled on the same cadence.
  const phases = useRunPhases(runId);
  // The cross-grain decision queue, narrowed to this run — polled while the run can still park one. Agent-grain
  // decisions key off the agent run id, so we pass the run's fanned-out agent ids (from the phase projection).
  const pendingPoll = run.data ? isRunActive(run.data.status) : true;
  const decisions = usePendingDecisions(pendingPoll);
  const runAgentIds = new Set((phases.data?.phases ?? []).flatMap((p) => p.agents).map((a) => a.agentRunId));
  const runDecisions = decisions.data ? decisionsForRun(decisions.data, runId, runAgentIds) : [];
  const workflowId = run.data?.workflowId ?? null;
  const replay = useReplayRun();

  const onReplay = async () => {
    const result = await replay.mutateAsync(runId);
    await navigate({
      to: "/teams/$teamSlug/runs/$runId",
      params: { teamSlug, runId: result.runId },
    });
  };

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 16 }}>
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/runs", params: { teamSlug } })}>Runs</a>
          <span className="sep">/</span>
          <span className="cur">Run {runId.slice(0, 8)}</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">Run {runId.slice(0, 8)}</h1>
            {run.data && phases.data && (
              <RunStateHeader runStatus={run.data.status} phases={phases.data.phases}
                pendingDecisions={decisions.data ? runDecisions.length : undefined} />
            )}
          </div>
          <div className="ct-actions">
            <button
              className="btn btn-primary"
              onClick={() => void onReplay()}
              disabled={replay.isPending || !run.data}
              title="Replay this run with the same release + trigger + plain variable snapshot. Secrets re-resolved from current."
            >
              <Ic.Play size={13} /> {replay.isPending ? "Queuing replay…" : "Re-run"}
            </button>
            {workflowId && (
              <button
                className="btn"
                onClick={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId", params: { teamSlug, workflowId } })}
              >
                <Ic.ArrowLeft size={13} /> Back to workflow
              </button>
            )}
          </div>
        </div>
      </div>

      <div className="ct-body run-room-body">
        <aside className="run-room-rail">
          <div className="rail-card">
            <div className="rail-card-head"><Ic.Workflow size={12} aria-hidden="true" /> Outline</div>
            {phases.data
              ? <RunOutline phases={phases.data.phases} />
              : <div className="run-outline-empty">{phases.isLoading ? "Loading outline…" : "Outline unavailable."}</div>}
          </div>
        </aside>
        <div className="run-room-main">
          {/* Framed as a panel so the center aligns with the left/right rail cards (same border + top edge),
              and the Activity·Canvas·Changes·Trace tabs read as that panel's header. */}
          <div className="run-panel">
            <RunDetailView
              runId={runId}
              onOpenRun={(childRunId) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId: childRunId } })}
            />
          </div>
        </div>
        <aside className="run-room-context">
          {decisions.data && <DecisionInbox decisions={runDecisions} />}
          {run.data && <RunFacts run={run.data} />}
        </aside>
      </div>
    </section>
  );
}
