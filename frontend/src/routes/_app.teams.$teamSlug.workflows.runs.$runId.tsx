import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { RunDetailView } from "@/components/workflows/RunDetailView";
import { useReplayRun, useWorkflow, useWorkflowRun } from "@/hooks/use-workflows";

/**
 * Standalone run-detail page (reached from the workflows list → Runs). The run content itself
 * is the shared <see cref="RunDetailView"/>, which is ALSO rendered inside the editor's in-page
 * run dialog — this route just wraps it with page chrome (breadcrumb + Re-run / Back).
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/runs/$runId")({
  component: WorkflowRunDetailPage,
});

function WorkflowRunDetailPage() {
  const { teamSlug, runId } = Route.useParams();
  const navigate = useNavigate();
  // Shared with RunDetailView via the same query key (React Query dedups) — used here only to
  // resolve the breadcrumb workflow + Re-run target.
  const run = useWorkflowRun(runId);
  const workflowId = run.data?.workflowId ?? null;
  const workflow = useWorkflow(workflowId);
  const replay = useReplayRun();

  const onReplay = async () => {
    const result = await replay.mutateAsync(runId);
    await navigate({
      to: "/teams/$teamSlug/workflows/runs/$runId",
      params: { teamSlug, runId: result.runId },
    });
  };

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 16 }}>
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/workflows", params: { teamSlug } })}>Workflows</a>
          <span className="sep">/</span>
          {workflowId ? (
            <a onClick={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId", params: { teamSlug, workflowId } })}>
              {workflow.data?.name ?? "Workflow"}
            </a>
          ) : <span>Workflow</span>}
          <span className="sep">/</span>
          {workflowId ? (
            <a onClick={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId/runs", params: { teamSlug, workflowId } })}>Runs</a>
          ) : <span>Runs</span>}
          <span className="sep">/</span>
          <span className="cur">Run {runId.slice(0, 8)}</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">Run {runId.slice(0, 8)}</h1>
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

      <div className="ct-body">
        <RunDetailView runId={runId} />
      </div>
    </section>
  );
}
