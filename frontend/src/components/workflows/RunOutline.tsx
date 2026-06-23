import { Ic } from "@/_imported/ai-code-space/icons";
import type { PhaseAgentRef, PhaseStatus, RunPhase } from "@/api/workflows";

import { isAgentBusy } from "./runPhases";

/**
 * The run's navigation skeleton — the phase tree, the run-neutral projection (GET /runs/{id}/phases). One row per
 * phase (a node, a map fan-out, an agent step, a supervisor decision, a model-authored phase), each carrying its
 * status, a small agent roll-up, and the agent runs it fanned out to (indented). It collapses naturally: a
 * single-agent run is ~3 rows, a workflow is its node tree, a Deep supervisor is its phases — same data, same
 * component. When `onSelectAgent` is given the indented agent rows become buttons that focus the matching card in
 * the center (the selected one is highlighted). Polls in lockstep with the rest of the run view.
 */
export function RunOutline({ phases, selectedAgentRunId, onSelectAgent }: {
  phases: readonly RunPhase[];
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string) => void;
}) {
  if (phases.length === 0) {
    return <div className="run-outline-empty">No phases yet — the run hasn’t reached a step.</div>;
  }

  return (
    <nav className="run-outline" aria-label="Run outline">
      {phases.map((p) => <PhaseRow key={p.id} phase={p} selectedAgentRunId={selectedAgentRunId} onSelectAgent={onSelectAgent} />)}
    </nav>
  );
}

function PhaseRow({ phase, selectedAgentRunId, onSelectAgent }: { phase: RunPhase; selectedAgentRunId?: string | null; onSelectAgent?: (agentRunId: string) => void }) {
  const { agentCount, succeededCount, failedCount } = phase.metrics;

  return (
    <div className="run-outline-phase" data-status={phase.status.toLowerCase()}>
      <div className="run-outline-row">
        <span className="run-outline-glyph" data-status={phase.status.toLowerCase()} aria-hidden="true"><PhaseGlyph status={phase.status} /></span>
        <span className="run-outline-label" title={phase.label}>{phase.label}</span>
        {agentCount > 0 && (
          <span className="run-outline-metric">
            {succeededCount}/{agentCount}
            {failedCount > 0 && <span className="run-outline-metric-fail">· {failedCount} failed</span>}
          </span>
        )}
      </div>

      {phase.summary && <div className="run-outline-summary" title={phase.summary}>{phase.summary}</div>}

      {phase.agents.length > 0 && (
        <ul className="run-outline-agents">
          {phase.agents.map((a, i) => (
            <li key={`${a.agentRunId}:${i}`}>
              {onSelectAgent ? (
                <button type="button" className="run-outline-agent" aria-pressed={a.agentRunId === selectedAgentRunId} data-selected={a.agentRunId === selectedAgentRunId || undefined} onClick={() => onSelectAgent(a.agentRunId)}>
                  <AgentRowInner agent={a} />
                </button>
              ) : (
                <div className="run-outline-agent"><AgentRowInner agent={a} /></div>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function AgentRowInner({ agent }: { agent: PhaseAgentRef }) {
  return (
    <>
      <span className="run-outline-agent-dot" data-busy={isAgentBusy(agent.status) || undefined} data-status={agent.status.toLowerCase()} aria-hidden="true" />
      <span className="run-outline-agent-label">{agent.label ?? agent.iterationKey ?? agent.agentRunId.slice(0, 8)}</span>
      <span className="run-outline-agent-status">{agent.status}</span>
    </>
  );
}

/** A phase's status glyph — the same six-state vocabulary the run canvas uses (Active is a spinner, Pending a hollow ring). */
function PhaseGlyph({ status }: { status: PhaseStatus }) {
  if (status === "Succeeded") return <Ic.Check size={13} />;
  if (status === "Failed") return <Ic.X size={13} />;
  if (status === "Waiting") return <Ic.Pause size={12} />;
  if (status === "Active") return <span className="run-outline-spin" />;
  if (status === "Skipped") return <Ic.Dot size={15} />;
  return <span className="run-outline-hollow" />;   // Pending
}
