import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowDetail } from "@/api/workflows";

/**
 * Agent "Settings" — the manage surface, deliberately distinct from the Overview dashboard. Where
 * Overview answers "what is this agent + what's it been doing" (identity, triggers, recent runs),
 * Settings is a plain, sectioned list of CONTROLS: the Enabled/Paused switch, the (forthcoming)
 * guardrails, and the danger zone. No identity header — the name lives in the breadcrumb and on
 * Overview — so the two tabs never read as duplicates.
 *
 * Pure / presentational: the parent loads the workflow and owns the enable/delete handlers (each
 * backed by its own dedicated endpoint, so nothing here touches the canvas definition).
 */
export function AgentSettingsPanel({ workflow, onToggleEnabled, onDelete, toggling = false, deleting = false }: {
  workflow: WorkflowDetail;
  onToggleEnabled: () => void;
  onDelete: () => void;
  toggling?: boolean;
  deleting?: boolean;
}) {
  return (
    <div className="agent-set">
      <section className="agent-set-group">
        <h2 className="agent-set-group-title">General</h2>
        <div className="agent-set-row">
          <div className="agent-set-row-text">
            <div className="agent-set-row-title">Status</div>
            <div className="agent-set-row-desc">
              {workflow.enabled
                ? "Enabled — its triggers fire and it can run."
                : "Paused — triggers won't fire until you enable it."}
            </div>
          </div>
          <button className="btn" onClick={onToggleEnabled} disabled={toggling}>
            {workflow.enabled ? <><Ic.Pause size={13} /> Pause agent</> : <><Ic.Play size={13} /> Enable agent</>}
          </button>
        </div>
      </section>

      <section className="agent-set-group">
        <h2 className="agent-set-group-title">Guardrails</h2>
        <div className="agent-set-soon">
          <span className="agent-set-soon-ic"><Ic.Lock size={15} /></span>
          <p className="agent-set-soon-text">Restrict which repositories this agent may touch, which actions it can take, and require human approval before it writes anything back.</p>
          <span className="agent-set-soon-tag">Coming soon</span>
        </div>
      </section>

      <section className="agent-set-group">
        <h2 className="agent-set-group-title">Danger zone</h2>
        <div className="agent-set-row agent-set-danger">
          <div className="agent-set-row-text">
            <div className="agent-set-row-title">Delete this agent</div>
            <div className="agent-set-row-desc">Removes the agent and stops its triggers. Runs already in flight finish on their own.</div>
          </div>
          <button className="btn btn-danger" onClick={onDelete} disabled={deleting}>
            <Ic.Trash size={13} /> {deleting ? "Deleting…" : "Delete agent"}
          </button>
        </div>
      </section>
    </div>
  );
}
