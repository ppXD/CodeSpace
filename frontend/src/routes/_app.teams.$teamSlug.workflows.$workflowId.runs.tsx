import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { RunWorkflowModal } from "@/components/workflows/RunWorkflowModal";
import {
  useRunWorkflowManually,
  useWorkflow,
  useWorkflowRuns,
} from "@/hooks/use-workflows";

/**
 * Workflow-scoped runs history at `/workflows/{id}/runs`. The canvas owns the
 * primary detail view (Dify-style); this page is the sibling "execution log"
 * the operator opens from the canvas's "Runs" button.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/$workflowId/runs")({
  component: WorkflowRunsPage,
});

function WorkflowRunsPage() {
  const { teamSlug, workflowId } = Route.useParams();
  const navigate = useNavigate();
  const workflow = useWorkflow(workflowId);
  const runs = useWorkflowRuns(workflowId);
  const runManually = useRunWorkflowManually();
  const [runModalOpen, setRunModalOpen] = useState(false);

  const rows = runs.data ?? [];
  const inputs = workflow.data?.definition.inputs ?? [];

  const startRun = async (payload?: Record<string, unknown>) => {
    const result = await runManually.mutateAsync({ workflowId, payload });
    setRunModalOpen(false);
    navigate({
      to: "/teams/$teamSlug/workflows/runs/$runId",
      params: { teamSlug, runId: result.runId },
    });
  };

  // Inputs declared → collect them in a form first (the Dify "fill, then run" step);
  // otherwise run immediately. Swallow the rejection so a failed run keeps the modal open
  // with its error visible instead of surfacing an unhandled promise rejection.
  const handleRun = () => {
    if (inputs.length === 0) { void startRun().catch(() => {}); return; }
    runManually.reset();
    setRunModalOpen(true);
  };

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 14 }}>
        <div className="ct-crumbs">
          <a onClick={() => navigate({ to: "/teams/$teamSlug/workflows", params: { teamSlug } })}>Workflows</a>
          <span className="sep">/</span>
          <a onClick={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId", params: { teamSlug, workflowId } })}>
            {workflow.data?.name ?? "Workflow"}
          </a>
          <span className="sep">/</span>
          <span className="cur">Activity</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">Activity</h1>
          <div className="ct-actions">
            <button
              className="btn"
              onClick={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId", params: { teamSlug, workflowId } })}
            >
              <Ic.ArrowLeft size={13} /> Back to canvas
            </button>
            <button className="btn btn-primary" onClick={handleRun} disabled={runManually.isPending}>
              <Ic.Play size={13} /> {runManually.isPending ? "Starting…" : "Run now"}
            </button>
          </div>
        </div>
      </div>

      <div className="ct-body">
        {workflow.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Workflow not found</div>
            <div className="cn-banner-p">{workflow.error.message}</div>
          </div>
        )}

        {runs.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading activity…</div></div>
        )}

        {!runs.isLoading && rows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No activity yet</div>
            <div className="ct-empty-p">
              Click <strong>Run now</strong> above, or wait for a matching trigger to fire.
            </div>
          </div>
        )}

        {!runs.isLoading && rows.length > 0 && (
          <table className="tbl">
            <thead>
              <tr>
                <th>Status</th>
                <th>Trigger</th>
                <th>Started</th>
                <th>Duration</th>
                <th>Version</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr
                  key={r.id}
                  onClick={() =>
                    navigate({
                      to: "/teams/$teamSlug/workflows/runs/$runId",
                      params: { teamSlug, runId: r.id },
                    })
                  }
                >
                  <td><RunStatusBadge status={r.status} /></td>
                  <td><span className="wf-trigger-chip wf-trigger-chip-soft">{r.sourceType}</span></td>
                  <td>{r.startedAt ? formatTime(r.startedAt) : "—"}</td>
                  <td>{formatDuration(r.startedAt, r.completedAt)}</td>
                  <td><span className="wf-version">v{r.workflowVersion}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {runModalOpen && (
        <RunWorkflowModal
          workflowName={workflow.data?.name ?? "workflow"}
          inputs={inputs}
          pending={runManually.isPending}
          error={runManually.error instanceof ApiError ? runManually.error.message : null}
          onRun={(payload) => { void startRun(payload).catch(() => {}); }}
          onClose={() => setRunModalOpen(false)}
        />
      )}
    </section>
  );
}

function RunStatusBadge({ status }: { status: string }) {
  // Enqueued visually distinct from Running so operators can spot Hangfire-side stalls.
  const tone =
    status === "Success" ? "ok"
    : status === "Failure" ? "err"
    : status === "Cancelled" ? "muted"
    : status === "Enqueued" ? "queued"
    : status === "Suspended" ? "suspended"
    : "running";

  return <span className={`wf-status-pill wf-status-${tone}`}>{status}</span>;
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString();
}

function formatDuration(startIso: string | null, endIso: string | null): string {
  if (!startIso) return "—";
  const start = new Date(startIso).getTime();
  const end = endIso ? new Date(endIso).getTime() : Date.now();
  const seconds = Math.max(0, Math.floor((end - start) / 1000));

  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}
