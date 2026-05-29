import { ApiError } from "@/api/request";
import { useWorkflowRun } from "@/hooks/use-workflows";

/**
 * Shared run-detail view: status summary + normalized payload + declared outputs + the
 * per-node execution trace for one workflow run. Fetches by id and auto-polls while the run
 * is non-terminal (via useWorkflowRun).
 *
 * Rendered both on the standalone run-detail route AND inside the editor's in-page run dialog,
 * so the two never drift. It deliberately uses the `.acs-root`-scoped `.wf-*` styles, so any
 * host must live inside `.acs-root` (the route does; the editor overlay renders in-tree rather
 * than portaling to <body> for exactly this reason).
 */
export function RunDetailView({ runId }: { runId: string }) {
  const run = useWorkflowRun(runId);

  if (run.isLoading) {
    return <div className="ct-empty"><div className="ct-empty-h">Loading run…</div></div>;
  }

  if (run.error instanceof ApiError || !run.data) {
    return (
      <div className="cn-banner cn-banner-err" style={{ margin: 0 }}>
        <div className="cn-banner-h">Run not found</div>
        <div className="cn-banner-p">{run.error instanceof ApiError ? run.error.message : "It may have been removed."}</div>
      </div>
    );
  }

  const r = run.data;

  return (
    <div className="wf-detail-body">
      <div className="wf-run-summary">
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

      {r.error && (
        <div className="cn-banner cn-banner-err" style={{ margin: "0 0 16px" }}>
          <div className="cn-banner-h">Run failed</div>
          <div className="cn-banner-p" style={{ fontFamily: "inherit" }}>{r.error}</div>
        </div>
      )}

      <section className="wf-section">
        <h2 className="wf-section-h">Normalized payload</h2>
        <pre className="wf-json">{JSON.stringify(r.normalizedPayload, null, 2)}</pre>
      </section>

      {/* The run's declared Outputs (the Terminal's resolved inputs) — what this run produced.
          Only shown once the run reached a successful Terminal. */}
      {hasContent(r.outputs) && (
        <section className="wf-section">
          <h2 className="wf-section-h">Outputs</h2>
          <pre className="wf-json">{JSON.stringify(r.outputs, null, 2)}</pre>
        </section>
      )}

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
                {/* A trigger node consumes nothing (Inputs is genuinely {}), so hide the empty
                    block — the entered values surface under Outputs + NORMALIZED PAYLOAD. */}
                {hasContent(n.inputs) && (
                  <details className="wf-run-node-io">
                    <summary>Inputs</summary>
                    <pre className="wf-json">{JSON.stringify(n.inputs, null, 2)}</pre>
                  </details>
                )}
                {hasContent(n.outputs) && (
                  <details className="wf-run-node-io">
                    <summary>Outputs</summary>
                    <pre className="wf-json">{JSON.stringify(n.outputs, null, 2)}</pre>
                  </details>
                )}
                {!n.error && !hasContent(n.inputs) && !hasContent(n.outputs) && (
                  <div className="wf-run-node-none">No inputs or outputs recorded.</div>
                )}
              </li>
            ))}
          </ol>
        )}
      </section>
    </div>
  );
}

/** Status pill shared across the run-detail view + run lists. */
export function RunStatusBadge({ status }: { status: string }) {
  // Enqueued = "claimed by dispatcher, waiting for worker pickup".
  const tone =
    status === "Success" ? "ok"
    : status === "Failure" ? "err"
    : status === "Cancelled" || status === "Skipped" ? "muted"
    : status === "Enqueued" ? "queued"
    : "running";

  return <span className={`wf-status-pill wf-status-${tone}`}>{status}</span>;
}

/** True when a value is worth a dedicated block — non-null, and not an empty object. */
function hasContent(value: unknown): boolean {
  if (value === null || value === undefined) return false;
  if (typeof value === "object" && !Array.isArray(value)) return Object.keys(value).length > 0;
  return true;
}
