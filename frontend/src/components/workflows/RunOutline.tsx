import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PhaseAgentRef, PhaseStatus, RunPhase } from "@/api/workflows";

import { buildWaves, waveBreakdown } from "./runActivity";

const NODE_SOURCE = "node-summary";

/**
 * The run's navigation skeleton — answers WHERE the run is + WHO's running. It renders the structural node spine
 * (manual / code / end), and when a supervisor authored semantic phases it nests a labeled "Phases" block under the
 * node that spawned them (Ground / Implement / Review). Each agent-bearing phase is a row — status glyph + label +
 * done/total + a chevron that collapses its compact agent list (name + status, no metrics). Clicking a PHASE focuses
 * it in the center (the Activity tiles filter to it); clicking an AGENT focuses its phase AND opens its terminal. The
 * Tokens/Tools/Time rollup lives on the tile + the full terminal, never here. Polls in lockstep with the rest of the view.
 */
export function RunOutline({ phases, selectedPhaseId, onSelectPhase, selectedAgentRunId, onSelectAgent }: {
  phases: readonly RunPhase[];
  selectedPhaseId?: string | null;
  onSelectPhase?: (phaseId: string | null) => void;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
}) {
  if (phases.length === 0) {
    return <div className="run-outline-empty">No phases yet — the run hasn’t reached a step.</div>;
  }

  // buildWaves assigns each agent to exactly ONE phase (an authored phase wins over the node / decision that also lists
  // it), so a supervisor agent shows under its semantic phase, never doubled under the node — and EVERY agent lands in
  // some wave, so none is dropped.
  const waves = buildWaves(phases);
  const agentsByPhase = new Map(waves.map((w) => [w.id, w.agents]));
  const waveIds = new Set(waves.map((w) => w.id));

  const nodes = phases.filter((p) => p.sourceKey === NODE_SOURCE);
  // The supervisor's WORK phases — the agent-owning supervisor-ledger phases (the model-authored ones when the plan
  // grouped its subtasks, else the spawn/retry waves for a flat plan). Decision rows that own no agent (plan / stop)
  // are excluded, so the block stays the phases-with-work, never the raw decision tape.
  const workPhases = phases.filter((p) => p.sourceKey !== NODE_SOURCE && waveIds.has(p.id));

  // Slot the "Phases" block before the terminal (the agentless last node), so it reads manual / code / Phases / end.
  const terminal = nodes.length > 1 && (agentsByPhase.get(nodes.at(-1)!.id)?.length ?? 0) === 0 ? nodes.at(-1)! : null;
  const spine = terminal ? nodes.slice(0, -1) : nodes;

  const sel = { selectedPhaseId, onSelectPhase, selectedAgentRunId, onSelectAgent };

  return (
    <nav className="run-outline" aria-label="Run outline">
      {spine.map((node) => <PhaseRow key={node.id} phase={node} agents={agentsByPhase.get(node.id) ?? []} {...sel} />)}

      {workPhases.length > 0 && (
        <div className="run-outline-phases">
          <div className="run-outline-phases-label">Phases</div>
          {workPhases.map((p) => <PhaseRow key={p.id} phase={p} agents={agentsByPhase.get(p.id) ?? []} {...sel} />)}
        </div>
      )}

      {terminal && <PhaseRow key={terminal.id} phase={terminal} agents={[]} {...sel} />}
    </nav>
  );
}

function PhaseRow({ phase, agents, selectedPhaseId, onSelectPhase, selectedAgentRunId, onSelectAgent }: {
  phase: RunPhase;
  agents: PhaseAgentRef[];
  selectedPhaseId?: string | null;
  onSelectPhase?: (phaseId: string | null) => void;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
}) {
  const hasAgents = agents.length > 0;
  const [open, setOpen] = useState(() => phase.status === "Active");   // the active phase starts expanded; the rest collapsed
  const selected = phase.id === selectedPhaseId;
  const b = waveBreakdown(agents);

  const selectAgent = (id: string) => {
    onSelectPhase?.(phase.id);   // focus the agent's phase too, so the Activity tiles show it
    onSelectAgent?.(id === selectedAgentRunId ? null : id);
  };

  return (
    <div className="run-outline-phase" data-status={phase.status.toLowerCase()} data-selected={selected || undefined}>
      <div className="run-outline-row">
        {hasAgents
          ? <button type="button" className="run-outline-caret" data-open={open || undefined} aria-expanded={open} aria-label="Toggle agents" onClick={() => setOpen((v) => !v)}><Ic.ChevronRight size={12} /></button>
          : <span className="run-outline-caret-spacer" aria-hidden="true" />}

        <span className="run-outline-glyph" data-status={phase.status.toLowerCase()} aria-hidden="true"><PhaseGlyph status={phase.status} /></span>

        {hasAgents && onSelectPhase
          ? <button type="button" className="run-outline-label" aria-pressed={selected} title={phase.label} onClick={() => onSelectPhase(selected ? null : phase.id)}>{phase.label}</button>
          : <span className="run-outline-label" title={phase.label}>{phase.label}</span>}

        {hasAgents && (
          <span className="run-outline-metric">{b.done}/{b.total}{b.failed > 0 && <span className="run-outline-metric-fail"> · {b.failed}✕</span>}</span>
        )}
      </div>

      {phase.summary && <div className="run-outline-summary" title={phase.summary}>{phase.summary}</div>}

      {hasAgents && open && (
        <ul className="run-outline-agents">
          {agents.map((a) => (
            <li key={a.agentRunId}>
              {onSelectAgent
                ? <button type="button" className="run-outline-agent" aria-pressed={a.agentRunId === selectedAgentRunId} data-selected={a.agentRunId === selectedAgentRunId || undefined} onClick={() => selectAgent(a.agentRunId)}><AgentRowInner agent={a} /></button>
                : <div className="run-outline-agent"><AgentRowInner agent={a} /></div>}
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
      {/* data-busy drives the blue PULSE — Running only; a Queued agent must read amber (data-status), not the busy blue. */}
      <span className="run-outline-agent-dot" data-busy={agent.status === "Running" || undefined} data-status={agent.status.toLowerCase()} aria-hidden="true" />
      <span className="run-outline-agent-label">{agent.label ?? agent.iterationKey ?? agent.agentRunId.slice(0, 8)}</span>
      <span className="run-outline-agent-status">{agent.status.toLowerCase()}</span>
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
