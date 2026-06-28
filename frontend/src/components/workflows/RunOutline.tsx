import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PhaseAgentRef, PhaseStatus, RunPhase } from "@/api/workflows";

import { buildWaves, formatDuration, tileState } from "./runActivity";

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

function PhaseRow({ phase, agents, onSelectPhase, selectedAgentRunId, onSelectAgent }: {
  phase: RunPhase;
  agents: PhaseAgentRef[];
  onSelectPhase?: (phaseId: string | null) => void;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
}) {
  // Agentless node (a trigger / terminal / structural step) → a plain status row, no box.
  if (agents.length === 0) {
    return (
      <div className="run-outline-phase" data-status={phase.status.toLowerCase()}>
        <div className="run-outline-row">
          <span className="run-outline-glyph" data-status={phase.status.toLowerCase()} aria-hidden="true"><PhaseGlyph status={phase.status} /></span>
          <span className="run-outline-label" title={phase.label}>{phase.label}</span>
        </div>
      </div>
    );
  }

  return <PhaseBox phase={phase} agents={agents} onSelectPhase={onSelectPhase} selectedAgentRunId={selectedAgentRunId} onSelectAgent={onSelectAgent} />;
}

/**
 * An agent-bearing phase as a CLAUDE-style framed box — the header toggles its agent list (name top-left, chevron
 * top-right, a square status dot per agent below). Toggling also focuses the phase so the Activity timeline scrolls to
 * it. Each agent is one full-width Agent·Time row (the WHOLE row is the click target; the rich rollup lives in the terminal).
 */
function PhaseBox({ phase, agents, onSelectPhase, selectedAgentRunId, onSelectAgent }: {
  phase: RunPhase;
  agents: PhaseAgentRef[];
  onSelectPhase?: (phaseId: string | null) => void;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
}) {
  const [open, setOpen] = useState(() => phase.status === "Active");

  const toggle = () => { setOpen((v) => !v); onSelectPhase?.(phase.id); };
  const focusAgent = (id: string) => { onSelectPhase?.(phase.id); onSelectAgent?.(id === selectedAgentRunId ? null : id); };

  return (
    <div className="run-outline-phase">
      {/* one gray card — the SAME gray clicked or not; the header toggles, the agent table lives INSIDE it, only a row highlights */}
      <div className="run-outline-box">
        <button type="button" className="run-outline-box-head" data-open={open || undefined} aria-expanded={open} onClick={toggle}>
          <span className="run-outline-box-headrow">
            <span className="run-outline-box-name" title={phase.label}>{phase.label}</span>
            <Ic.ChevronRight className="run-outline-box-caret" size={13} aria-hidden="true" />
          </span>
          <span className="run-outline-dots" aria-hidden="true">
            {agents.map((a) => <i key={a.agentRunId} data-state={tileState(a.status)} />)}
          </span>
        </button>

        {open && (
          <div className="run-outline-aglist">
            <div className="run-outline-aghead"><span>Agent</span><span>Time</span></div>
            {agents.map((a) => {
              // Name each agent by the most meaningful authored string — the supervisor's model-authored ROLE, else a
              // plain node / map agent's GOAL title (so a fan-out branch reads as its subtask, not a structural map#N
              // key) — unified across run types. Truncated to one line (.run-outline-agname ellipsis); the full text
              // shows on hover via title= below. Falls back to label / iterationKey / short id when none is present.
              const name = a.role ?? a.goal ?? a.label ?? a.iterationKey ?? a.agentRunId.slice(0, 8);
              const selected = a.agentRunId === selectedAgentRunId;
              const body = (<><span className="run-outline-agname">{name}</span><span className="run-outline-agtime">{formatDuration(a.durationMs)}</span></>);

              // The WHOLE row is one click target (no inner button, no hover underline); a plain div when not selectable.
              return onSelectAgent
                ? <button key={a.agentRunId} type="button" className="run-outline-agrow" data-selected={selected || undefined} aria-pressed={selected} title={name} onClick={() => focusAgent(a.agentRunId)}>{body}</button>
                : <div key={a.agentRunId} className="run-outline-agrow" data-selected={selected || undefined} title={name}>{body}</div>;
            })}
          </div>
        )}
      </div>
    </div>
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
