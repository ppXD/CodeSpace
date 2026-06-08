import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowDetail, WorkflowRunSummary } from "@/api/workflows";

import { RunStatusBadge } from "./RunDetailView";

/**
 * Agent "home" — a read-only summary of what the agent is, how it's governed, and what it's been
 * doing, plus the two primary actions (Run now · Edit in Source). The agent-first landing: a
 * beginner sees the description, triggers, status, and recent runs at a glance — not the raw canvas.
 * Pure / presentational (the parent loads the workflow + recent runs and owns the action handlers).
 */
export function AgentOverviewPanel({ workflow, recentRuns = [], onRun, onEditSource, onViewActivity, running = false }: {
  workflow: WorkflowDetail;
  recentRuns?: ReadonlyArray<WorkflowRunSummary>;
  onRun: () => void;
  onEditSource: () => void;
  onViewActivity?: () => void;
  running?: boolean;
}) {
  const triggers = workflow.activations.map((a) => a.typeKey);
  const shown = recentRuns.slice(0, 5);

  return (
    <div className="agent-ov">
      <header className="agent-ov-head">
        <div className="agent-ov-head-text">
          <h1 className="agent-ov-name">{workflow.name}</h1>
          {workflow.description
            ? <p className="agent-ov-desc">{workflow.description}</p>
            : <p className="agent-ov-desc agent-ov-desc-empty">No description yet.</p>}
        </div>
        <div className="agent-ov-actions">
          <button className="btn btn-primary" onClick={onRun} disabled={running}>
            <Ic.Play size={13} /> {running ? "Running…" : "Run now"}
          </button>
          <button className="btn" onClick={onEditSource}>
            <Ic.Workflow size={13} /> Edit in Source
          </button>
        </div>
      </header>

      <dl className="agent-ov-meta">
        <div className="agent-ov-meta-item">
          <dt>Runs when</dt>
          <dd>
            {triggers.length === 0
              ? <span className="agent-ov-muted">Manual only</span>
              : <span className="agent-ov-triggers">{triggers.map((t) => <span key={t} className="wf-trigger-chip">{t}</span>)}</span>}
          </dd>
        </div>
        <div className="agent-ov-meta-item">
          <dt>Status</dt>
          <dd>
            <span className="agent-ov-status" data-enabled={workflow.enabled}>{workflow.enabled ? "Enabled" : "Paused"}</span>
            <span className="agent-ov-ver">v{workflow.latestVersion}</span>
          </dd>
        </div>
      </dl>

      <section className="agent-ov-section">
        <div className="agent-ov-section-head">
          <h2 className="agent-ov-section-title">Recent activity</h2>
          {shown.length > 0 && onViewActivity && (
            <button type="button" className="agent-ov-link" onClick={onViewActivity}>View all <Ic.ChevronRight size={11} /></button>
          )}
        </div>
        {shown.length === 0 ? (
          <div className="agent-ov-runs-empty">No runs yet — click <strong>Run now</strong> or wait for a trigger to fire.</div>
        ) : (
          <ul className="agent-ov-runs">
            {shown.map((r) => (
              <li key={r.id} className="agent-ov-run">
                <RunStatusBadge status={r.status} />
                <span className="agent-ov-run-id">{r.id.slice(0, 8)}</span>
                <span className="agent-ov-run-src">{r.sourceType}</span>
                <span className="agent-ov-run-time">{r.startedAt ? new Date(r.startedAt).toLocaleString() : "—"}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
