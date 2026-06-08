import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowDetail } from "@/api/workflows";

/**
 * Agent "home" — a read-only summary of what the agent is and how it's governed, plus the two
 * primary actions (Run now · Edit in Source). This is the agent-first landing: a beginner sees
 * what it does, when it runs, and its status at a glance — not the raw node canvas. Pure /
 * presentational: the parent owns data-loading and the action handlers, so it's trivially testable.
 */
export function AgentOverviewPanel({ workflow, onRun, onEditSource, running = false }: {
  workflow: WorkflowDetail;
  onRun: () => void;
  onEditSource: () => void;
  running?: boolean;
}) {
  const triggers = workflow.activations.map((a) => a.typeKey);

  return (
    <div className="agent-ov">
      <div className="agent-ov-head">
        <h1 className="agent-ov-name">{workflow.name}</h1>
        {workflow.description
          ? <p className="agent-ov-desc">{workflow.description}</p>
          : <p className="agent-ov-desc agent-ov-desc-empty">No description yet.</p>}
      </div>

      <dl className="agent-ov-facts">
        <div className="agent-ov-fact">
          <dt>Runs when</dt>
          <dd>
            {triggers.length === 0
              ? <span className="agent-ov-muted">Manual only</span>
              : <span className="agent-ov-triggers">{triggers.map((t) => <span key={t} className="wf-trigger-chip">{t}</span>)}</span>}
          </dd>
        </div>
        <div className="agent-ov-fact">
          <dt>Status</dt>
          <dd>
            <span className="agent-ov-status" data-enabled={workflow.enabled}>{workflow.enabled ? "Enabled" : "Paused"}</span>
            <span className="agent-ov-ver">v{workflow.latestVersion}</span>
          </dd>
        </div>
      </dl>

      <div className="agent-ov-actions">
        <button className="btn btn-primary" onClick={onRun} disabled={running}>
          <Ic.Play size={13} /> {running ? "Running…" : "Run now"}
        </button>
        <button className="btn" onClick={onEditSource}>
          <Ic.Workflow size={13} /> Edit in Source
        </button>
      </div>
    </div>
  );
}
