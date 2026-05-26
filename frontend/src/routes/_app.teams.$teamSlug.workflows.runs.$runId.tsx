import { createFileRoute, useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useReplayRun, useWorkflowRun } from "@/hooks/use-workflows";

/**
 * Single-run detail. One row per executed node with expandable inputs/outputs blocks.
 * Auto-polls every 2 s while non-terminal so the operator sees the run progress live.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/runs/$runId")({
  component: WorkflowRunDetailPage,
});

function WorkflowRunDetailPage() {
  const { teamSlug, runId } = Route.useParams();
  const navigate = useNavigate();
  const run = useWorkflowRun(runId);
  const replay = useReplayRun();

  const onReplay = async () => {
    // POST /runs/{id}/replay returns the new run id. Navigate the operator there so they
    // can watch the replay execute. The engine's replay path reuses the original's
    // workflow_version + trigger payload + plain variable snapshot, but re-resolves
    // secrets from the current variable table.
    const result = await replay.mutateAsync(runId);
    await navigate({
      to: "/teams/$teamSlug/workflows/runs/$runId",
      params: { teamSlug, runId: result.runId },
    });
  };

  if (run.isLoading) {
    return (
      <section className="ct">
        <div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Loading run…</div></div></div>
      </section>
    );
  }

  if (run.error instanceof ApiError || !run.data) {
    return (
      <section className="ct">
        <div className="ct-body">
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Run not found</div>
            <div className="cn-banner-p">{run.error?.message ?? "It may have been removed."}</div>
          </div>
        </div>
      </section>
    );
  }

  const r = run.data;

  return (
    <section className="ct">
      {/* paddingBottom on ct-head: this page has no .ct-tabs row to absorb the bottom
          spacing, so without it the right-side action buttons (and the metadata row)
          would sit flush against the divider line. Matches the same inline patch the
          workflows-list and runs-list pages apply. */}
      <div className="ct-head" style={{ paddingBottom: 16 }}>
        <div className="ct-crumbs">
          <span onClick={() => navigate({ to: "/teams/$teamSlug/workflows", params: { teamSlug } })} style={{ cursor: "pointer" }}>
            Workflows
          </span>
          <span>·</span>
          <span onClick={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId", params: { teamSlug, workflowId: r.workflowId } })} style={{ cursor: "pointer" }}>
            Workflow
          </span>
          <span>·</span>
          <span>Run {r.id.slice(0, 8)}</span>
        </div>
        <div className="ct-title-row">
          <div>
            <h1 className="ct-title">Run {r.id.slice(0, 8)}</h1>
            <div className="ct-sub wf-run-summary">
              <RunStatusBadge status={r.status} />
              <span>·</span>
              <span className="wf-trigger-chip wf-trigger-chip-soft">{r.sourceType}</span>
              <span>·</span>
              <span className="wf-version">v{r.workflowVersion}</span>
              {r.startedAt && (
                <>
                  <span>·</span>
                  <span>{new Date(r.startedAt).toLocaleString()}</span>
                </>
              )}
            </div>
          </div>
          <div className="ct-actions">
            <button
              className="btn btn-primary"
              onClick={() => void onReplay()}
              disabled={replay.isPending}
              title="Replay this run with the same release + trigger + plain variable snapshot. Secrets re-resolved from current."
            >
              <Ic.Play size={13} /> {replay.isPending ? "Queuing replay…" : "Re-run"}
            </button>
            <button
              className="btn"
              onClick={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId", params: { teamSlug, workflowId: r.workflowId } })}
            >
              <Ic.ArrowLeft size={13} /> Back to workflow
            </button>
          </div>
        </div>
      </div>

      <div className="ct-body wf-detail-body">
        {r.error && (
          <div className="cn-banner cn-banner-err" style={{ margin: "0 16px 16px" }}>
            <div className="cn-banner-h">Run failed</div>
            <div className="cn-banner-p" style={{ fontFamily: "inherit" }}>{r.error}</div>
          </div>
        )}

        <section className="wf-section">
          <h2 className="wf-section-h">Normalized payload</h2>
          <pre className="wf-json">{JSON.stringify(r.normalizedPayload, null, 2)}</pre>
        </section>

        <section className="wf-section">
          <h2 className="wf-section-h">Node execution</h2>
          {r.nodes.length === 0 ? (
            <div className="ct-empty">
              <div className="ct-empty-h">No nodes executed yet</div>
              <div className="ct-empty-p">The engine hasn't picked up this run from the outbox yet — refresh in a moment.</div>
            </div>
          ) : (
            <ol className="wf-run-nodes">
              {r.nodes.map((n) => (
                <li key={`${n.nodeId}:${n.iterationKey}`} className="wf-run-node">
                  <div className="wf-run-node-head">
                    <span className="wf-run-node-id">{n.nodeId}</span>
                    <RunStatusBadge status={n.status} />
                    {n.startedAt && (
                      <span className="wf-run-node-time">
                        {new Date(n.startedAt).toLocaleTimeString()}
                        {n.completedAt && ` → ${new Date(n.completedAt).toLocaleTimeString()}`}
                      </span>
                    )}
                  </div>
                  {n.error && <pre className="wf-json wf-json-err">{n.error}</pre>}
                  <details className="wf-run-node-io">
                    <summary>Inputs</summary>
                    <pre className="wf-json">{JSON.stringify(n.inputs, null, 2)}</pre>
                  </details>
                  <details className="wf-run-node-io">
                    <summary>Outputs</summary>
                    <pre className="wf-json">{JSON.stringify(n.outputs, null, 2)}</pre>
                  </details>
                </li>
              ))}
            </ol>
          )}
        </section>
      </div>
    </section>
  );
}

function RunStatusBadge({ status }: { status: string }) {
  // Enqueued = "claimed by dispatcher, waiting for worker pickup". See the matching badge
  // in the run-list route + the WorkflowRunStatus type's doc-comment.
  const tone =
    status === "Success" ? "ok"
    : status === "Failure" ? "err"
    : status === "Cancelled" || status === "Skipped" ? "muted"
    : status === "Enqueued" ? "queued"
    : "running";

  return <span className={`wf-status-pill wf-status-${tone}`}>{status}</span>;
}
