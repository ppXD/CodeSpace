import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunWaitInfo } from "@/api/workflows";
import { ApiError } from "@/api/request";
import { useResumeRun, useWorkflowRun } from "@/hooks/use-workflows";

import { JsonView } from "./JsonView";

/**
 * How many sub-workflow levels we embed inline before falling back to a plain id. The engine caps
 * real nesting at 8; embedding (each level is a live, polling RunDetailView) stops well before that.
 */
const MAX_EMBED_DEPTH = 3;

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
export function RunDetailView({ runId, nested = false, depth = 0 }: { runId: string; nested?: boolean; depth?: number }) {
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
    <div className={nested ? "wf-detail-body wf-detail-body-nested" : "wf-detail-body"}>
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
        <div className="cn-banner cn-banner-err" style={{ margin: 0 }}>
          <div className="cn-banner-h">Run failed</div>
          <div className="cn-banner-p" style={{ fontFamily: "inherit" }}>{r.error}</div>
        </div>
      )}

      {r.status === "Suspended" && r.pendingWait && (
        <SuspendedPanel runId={runId} wait={r.pendingWait} depth={depth} />
      )}

      <section className="wf-section">
        <h2 className="wf-section-h">Normalized payload</h2>
        <JsonView data={r.normalizedPayload} />
      </section>

      {/* The run's declared Outputs (the Terminal's resolved inputs) — what this run produced.
          Only shown once the run reached a successful Terminal. */}
      {hasContent(r.outputs) && (
        <section className="wf-section">
          <h2 className="wf-section-h">Outputs</h2>
          <JsonView data={r.outputs} />
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
                    <JsonView data={n.inputs} />
                  </details>
                )}
                {hasContent(n.outputs) && (
                  <details className="wf-run-node-io">
                    <summary>Outputs</summary>
                    <JsonView data={n.outputs} />
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
    : status === "Suspended" ? "suspended"
    : "running";

  return <span className={`wf-status-pill wf-status-${tone}`}>{status}</span>;
}

/** True when a value is worth a dedicated block — non-null, and not an empty object. */
function hasContent(value: unknown): boolean {
  if (value === null || value === undefined) return false;
  if (typeof value === "object" && !Array.isArray(value)) return Object.keys(value).length > 0;
  return true;
}

/**
 * The resume affordance for a Suspended run. An Approval wait gets approve/reject + an optional
 * comment (posts the decision, then the live poll shows the run continue). A Timer wait just
 * shows when it'll wake. A Callback wait shows the tokened URL. A Subworkflow wait embeds the live
 * child run inline — including ITS resume affordance, so e.g. an approval deep inside the child is
 * operable right here; resolving it completes the child, which auto-resumes this run.
 */
export function SuspendedPanel({ runId, wait, depth = 0 }: { runId: string; wait: WorkflowRunWaitInfo; depth?: number }) {
  const resume = useResumeRun(runId);
  const [comment, setComment] = useState("");

  if (wait.kind === "Subworkflow") {
    // The wait's token is the child run id (engine contract). Embed the child run-detail.
    return <SubworkflowWaitPanel childRunId={wait.token} depth={depth} />;
  }

  if (wait.kind === "Approval") {
    const prompt = readPrompt(wait.payload);
    const decide = (approved: boolean) => resume.mutate({ approved, comment: comment.trim() || undefined });

    return (
      <section className="wf-section wf-approval">
        <h2 className="wf-section-h">Waiting for approval</h2>
        {prompt && <div className="wf-approval-prompt">{prompt}</div>}
        <textarea
          className="wf-form-input wf-approval-comment"
          rows={2}
          placeholder="Comment (optional)"
          value={comment}
          onChange={(e) => setComment(e.target.value)}
          disabled={resume.isPending}
        />
        <div className="wf-approval-actions">
          <button className="btn btn-primary" onClick={() => decide(true)} disabled={resume.isPending}>Approve</button>
          <button className="btn" onClick={() => decide(false)} disabled={resume.isPending}>Reject</button>
        </div>
        {resume.isError && <div className="wf-approval-err">Couldn&apos;t submit — try again.</div>}
      </section>
    );
  }

  if (wait.kind === "Timer") {
    return (
      <section className="wf-section wf-approval">
        <h2 className="wf-section-h">Sleeping</h2>
        <div className="wf-approval-prompt">
          {wait.wakeAt ? `Resumes around ${new Date(wait.wakeAt).toLocaleTimeString()}.` : "Waiting on a timer."}
        </div>
      </section>
    );
  }

  if (wait.kind === "Callback") {
    const url = `${window.location.origin}/api/workflows/callbacks/${wait.token}`;
    return (
      <section className="wf-section wf-approval">
        <h2 className="wf-section-h">Waiting for callback</h2>
        <div className="wf-approval-prompt">An external system resumes this run by POSTing to:</div>
        <div className="wf-callback-row">
          <input className="wf-form-input wf-callback-url" readOnly value={url} onFocus={(e) => e.currentTarget.select()} />
          <button className="btn" onClick={() => navigator.clipboard?.writeText(url)}>Copy</button>
        </div>
      </section>
    );
  }

  return null;
}

/**
 * The Subworkflow-wait affordance: an expandable card that embeds the LIVE child run-detail
 * (recursively — it's a full RunDetailView). The child brings its own resume affordance, so an
 * approval / callback inside the child is operated right here; resolving it completes the child,
 * which the engine's completion hook turns into a resume of THIS run. Beyond a few nesting levels
 * we stop embedding (each level is a polling fetch) and just name the child run id.
 */
function SubworkflowWaitPanel({ childRunId, depth }: { childRunId: string; depth: number }) {
  const [open, setOpen] = useState(true);
  const canEmbed = depth < MAX_EMBED_DEPTH;

  return (
    <section className="wf-section wf-approval">
      <button
        type="button"
        className="wf-subrun-toggle"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        disabled={!canEmbed}
      >
        {canEmbed && (open ? <Ic.ChevronDown size={12} /> : <Ic.ChevronRight size={12} />)}
        <span className="wf-section-h">Running a sub-workflow</span>
      </button>
      <div className="wf-approval-prompt">
        This run is waiting for the sub-workflow below to finish. Acting on it here — e.g. approving —
        resumes this run automatically.
      </div>

      {!canEmbed ? (
        <div className="wf-approval-prompt">Sub-workflow run <code>{childRunId}</code> (nested too deep to embed).</div>
      ) : open && (
        <div className="wf-subrun">
          <RunDetailView runId={childRunId} nested depth={depth + 1} />
        </div>
      )}
    </section>
  );
}

/** Pull the approver-facing prompt out of a wait's suspend payload, if present. */
function readPrompt(payload: unknown): string {
  if (payload && typeof payload === "object" && "prompt" in payload) {
    const p = (payload as { prompt?: unknown }).prompt;
    return typeof p === "string" ? p : "";
  }
  return "";
}
