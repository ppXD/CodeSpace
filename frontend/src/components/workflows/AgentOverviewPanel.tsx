import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowDetail, WorkflowRunSummary } from "@/api/workflows";

import { RunStatusBadge } from "./RunDetailView";

/**
 * Agent "home" — a summary of what the agent is, how it's governed, and what it's been doing, plus its
 * lifecycle controls (Enable/Pause · Delete) and primary actions (Run now · Edit in Source) and the full
 * run list. The agent-first landing: a beginner sees the description, triggers, status, and every run at a
 * glance — not the raw canvas. Pure / presentational (the parent loads the workflow + runs and owns every
 * handler; the toggle acts immediately, delete confirms before acting; clicking a run row opens it via onOpenRun).
 */
export function AgentOverviewPanel({ workflow, runs = [], onRun, onEditSource, onOpenRun, onToggleEnabled, onDelete, running = false, toggling = false, deleting = false }: {
  workflow: WorkflowDetail;
  runs?: ReadonlyArray<WorkflowRunSummary>;
  onRun: () => void;
  onEditSource: () => void;
  onOpenRun?: (runId: string) => void;
  onToggleEnabled?: () => void;
  onDelete?: () => void;
  running?: boolean;
  toggling?: boolean;
  deleting?: boolean;
}) {
  const triggers = workflow.activations.map((a) => a.typeKey);

  return (
    <div className="agent-ov">
      <header className="agent-ov-head">
        <div className="agent-ov-head-text">
          <div className="agent-ov-name-row">
            <h1 className="agent-ov-name">{workflow.name}</h1>
            {/* Enable/Pause — a status pill beside the name that shows the current state AND the action
                (icon); clicking toggles immediately. It owns "is this workflow live?", so the meta block
                no longer repeats Status. */}
            {onToggleEnabled && (
              <button
                type="button"
                className="agent-ov-power"
                data-enabled={workflow.enabled}
                onClick={onToggleEnabled}
                disabled={toggling}
                title={workflow.enabled ? "Pause this workflow" : "Enable this workflow"}
                // Accessible name carries the ACTION (the icon does this for sighted users) while still
                // leading with the visible "Enabled"/"Paused" word, so it satisfies WCAG 2.5.3 (Label in Name).
                aria-label={workflow.enabled ? "Enabled — click to pause this workflow" : "Paused — click to enable this workflow"}
              >
                {workflow.enabled ? <Ic.Pause size={13} /> : <Ic.Play size={13} />}
                <span>{workflow.enabled ? "Enabled" : "Paused"}</span>
              </button>
            )}
          </div>
          {workflow.description
            ? <p className="agent-ov-desc">{workflow.description}</p>
            : <p className="agent-ov-desc agent-ov-desc-empty">No description yet.</p>}
        </div>
        <div className="agent-ov-actions">
          <button className="btn" onClick={onEditSource}>
            <Ic.Workflow size={13} /> Edit in Source
          </button>
          <button className="btn btn-primary" onClick={onRun} disabled={running}>
            <Ic.Play size={13} /> {running ? "Running…" : "Run now"}
          </button>
          {onDelete && (
            <button type="button" className="btn btn-danger" onClick={onDelete} disabled={deleting} title="Delete this workflow">
              <Ic.Trash size={13} /> {deleting ? "Deleting…" : "Delete workflow"}
            </button>
          )}
        </div>
      </header>

      <dl className="agent-ov-meta">
        <div className="agent-ov-meta-item">
          <dt>Runs when</dt>
          <dd>
            {triggers.length === 0
              ? <span className="wf-trigger-muted">Manual only</span>
              : <span className="agent-ov-triggers">{triggers.map((t) => <span key={t} className="wf-trigger-chip">{t}</span>)}</span>}
          </dd>
        </div>
        <div className="agent-ov-meta-item">
          <dt>Version</dt>
          <dd><span className="wf-version">v{workflow.latestVersion}</span></dd>
        </div>
      </dl>

      <section className="agent-ov-section">
        <div className="agent-ov-section-head">
          <h2 className="agent-ov-section-title">Runs</h2>
        </div>
        {runs.length === 0 ? (
          <div className="agent-ov-runs-empty">No runs yet — click <strong>Run now</strong> or wait for a trigger to fire.</div>
        ) : (
          <ul className="agent-activity">
            {runs.map((r) => (
              <li key={r.id} className="agent-activity-row" onClick={() => onOpenRun?.(r.id)} title={`Run ${r.id.slice(0, 8)}`}>
                <RunStatusBadge status={r.status} />
                <span className="agent-activity-id">{r.id.slice(0, 8)}</span>
                <span className="agent-activity-src">{r.sourceType}</span>
                <span className="agent-activity-time">{r.startedAt ? new Date(r.startedAt).toLocaleString() : "—"}</span>
                <span className="agent-activity-ver">v{r.workflowVersion}</span>
                <Ic.ChevronRight size={12} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
