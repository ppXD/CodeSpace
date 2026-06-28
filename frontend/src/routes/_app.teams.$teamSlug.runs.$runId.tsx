import { useMemo, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { RunAttemptsSummary } from "@/components/workflows/RunAttemptsSummary";
import { RerunProvenanceContext } from "@/components/workflows/rerunProvenanceContext";
import { rerunsByNode } from "@/components/workflows/runRerunProvenance";
import { DecisionInbox } from "@/components/workflows/DecisionInbox";
import { RunDetailView } from "@/components/workflows/RunDetailView";
import { RunFacts } from "@/components/workflows/RunFacts";
import { RunOutline } from "@/components/workflows/RunOutline";
import { RunStateHeader } from "@/components/workflows/RunStateHeader";
import { ContinueRunButton } from "@/components/workflows/ContinueRunButton";
import { StopRunButton } from "@/components/workflows/StopRunButton";
import { decisionsForRun } from "@/components/workflows/runDecisions";
import { isRunActive, usePendingDecisions, useReplayRun, useRunAttempts, useRunPhases, useWorkflowRun } from "@/hooks/use-workflows";

/**
 * The canonical run-detail page — the Run Room. A run is run-neutral (manual, scheduled, webhook, replay, task,
 * child), so it lives at the team level under /runs, NOT under any one workflow; everything that opens a run links
 * here. The run content is the shared <see cref="RunDetailView"/> (also rendered inline in the editor's run dialog);
 * this route wraps it with page chrome (breadcrumb + Re-run / Back to workflow) and the outline + decision rails.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/runs/$runId")({
  component: RunDetailPage,
});

// Remount per URL run id so the outline state resets cleanly on navigation (changing a path param does NOT remount
// by default).
function RunDetailPage() {
  const { teamSlug, runId } = Route.useParams();
  return <RunDetailRoom key={runId} teamSlug={teamSlug} runId={runId} />;
}

function RunDetailRoom({ teamSlug, runId }: { teamSlug: string; runId: string }) {
  const navigate = useNavigate();

  // ONE root-run view: a rerun forks a new attempt, but the detail always shows the lineage's CURRENT state (the
  // latest attempt — it already carries every node's result, reused + freshly re-run). The attempt ladder isn't a
  // page switcher; it feeds the per-node rerun history (each node shows which attempts re-ran it) + the informational
  // "N attempts" summary. `rootId` is the lineage identity the page titles on.
  const attempts = useRunAttempts(runId);
  const rootId = attempts.data?.rootRunId ?? runId;
  const ladder = attempts.data?.attempts;
  const latestRunId = ladder?.find((a) => a.isLatest)?.runId;
  const effectiveRunId = latestRunId ?? runId;
  // Memo on the ladder's CONTENT — useRunAttempts returns a fresh array on every 3s poll while a rerun is live, so a
  // `[attempts.data]` dep would rebuild the context (and re-render every node badge) each poll for no change.
  const provKey = (ladder ?? []).map((a) => `${a.runId}:${a.status}:${a.rerunFromNodeId ?? ""}`).join("|");
  // eslint-disable-next-line react-hooks/exhaustive-deps -- provKey is the content digest of `ladder`
  const provenance = useMemo(() => ({ attempts: ladder ?? [], rerunsByNode: rerunsByNode(ladder ?? []) }), [provKey]);

  // Shared with RunDetailView via the same query key (React Query dedups) — used here for the Re-run target,
  // the run-state header, and the "Back to workflow" link when the run came from an authored workflow.
  const run = useWorkflowRun(effectiveRunId);
  // The run-neutral outline (the phase projection) — a separate endpoint, polled on the same cadence.
  const phases = useRunPhases(effectiveRunId);
  // The cross-grain decision queue, narrowed to this run — polled while the run can still park one. Agent-grain
  // decisions key off the agent run id, so we pass the run's fanned-out agent ids (from the phase projection).
  const pendingPoll = run.data ? isRunActive(run.data.status) : true;
  const decisions = usePendingDecisions(pendingPoll);
  const runAgentIds = new Set((phases.data?.phases ?? []).flatMap((p) => p.agents).map((a) => a.agentRunId));
  const runDecisions = decisions.data ? decisionsForRun(decisions.data, effectiveRunId, runAgentIds) : [];
  const workflowId = run.data?.workflowId ?? null;
  const replay = useReplayRun();
  // The outline drives the center: a selected PHASE filters the Activity tiles to it; a selected AGENT opens its terminal.
  const [selectedAgentRunId, setSelectedAgentRunId] = useState<string | null>(null);
  const [selectedPhaseId, setSelectedPhaseId] = useState<string | null>(null);
  // Selecting a phase is a fresh focus — clear any open agent so its terminal can't linger after the tiles filter away
  // from it. (Selecting an AGENT sets its phase then itself, in that order, so the agent still wins.)
  const selectPhase = (phaseId: string | null) => { setSelectedPhaseId(phaseId); setSelectedAgentRunId(null); };

  const onReplay = async () => {
    const result = await replay.mutateAsync(effectiveRunId);
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
          <span className="cur">Run {rootId.slice(0, 8)}</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">Run {rootId.slice(0, 8)}</h1>
            {run.data && phases.data && (
              <RunStateHeader runStatus={run.data.status} phases={phases.data.phases}
                pendingDecisions={decisions.data ? runDecisions.length : undefined} />
            )}
            {attempts.data && <RunAttemptsSummary attempts={attempts.data.attempts} />}
          </div>
          <div className="ct-actions">
            {run.data && <StopRunButton runId={effectiveRunId} status={run.data.status} />}
            {run.data && <ContinueRunButton runId={effectiveRunId} status={run.data.status} hasPendingWait={run.data.pendingWait != null} />}
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

      {/* Share the lineage's rerun provenance down the whole detail so each node (Canvas + Activity) can show its own
          rerun history without prop-drilling. */}
      <RerunProvenanceContext.Provider value={provenance}>
        <div className="ct-body run-room-body">
          <aside className="run-room-rail">
            <div className="rail-card">
              <div className="rail-card-head"><Ic.Workflow size={12} aria-hidden="true" /> Outline</div>
              {phases.data
                ? <RunOutline phases={phases.data.phases} selectedPhaseId={selectedPhaseId} onSelectPhase={selectPhase} selectedAgentRunId={selectedAgentRunId} onSelectAgent={setSelectedAgentRunId} />
                : <div className="run-outline-empty">{phases.isLoading ? "Loading outline…" : "Outline unavailable."}</div>}
            </div>
          </aside>
          <div className="run-room-main">
            {/* Framed as a panel so the center aligns with the left/right rail cards (same border + top edge),
                and the Activity·Canvas·Changes·Trace tabs read as that panel's header. */}
            <div className="run-panel">
              <RunDetailView
                runId={effectiveRunId}
                selectedPhaseId={selectedPhaseId}
                selectedAgentRunId={selectedAgentRunId}
                onSelectAgent={setSelectedAgentRunId}
                onOpenRun={(childRunId) => navigate({ to: "/teams/$teamSlug/runs/$runId", params: { teamSlug, runId: childRunId } })}
              />
            </div>
          </div>
          <aside className="run-room-context">
            {decisions.data && <DecisionInbox decisions={runDecisions} />}
            {run.data && <RunFacts run={run.data} />}
          </aside>
        </div>
      </RerunProvenanceContext.Provider>
    </section>
  );
}
