import { useEffect, useRef } from "react";

import type { PhaseAgentRef } from "@/api/workflows";

import { AgentTerminal } from "./AgentTerminal";
import { AgentTile } from "./AgentTile";
import { tileState, waveBreakdown, type AgentWave, type WaveBreakdown } from "./runActivity";

/**
 * One phase on the Activity timeline — a wave of a phase's agents at its execution point. A SINGLE-agent wave skips the
 * tile layer and shows that agent's full terminal directly (a Fast run reads Run started → one terminal → done). A
 * MULTI-agent wave shows a phase marker (label + a status dot per agent + a live summary) over a grid of terminal
 * tiles; clicking a tile opens its full terminal below — one open per wave, lifted to the route so the outline and the
 * timeline share the selection. Scrolls into view when the outline selects this phase.
 */
export function TimelinePhase({ wave, selectedPhaseId, selectedAgentRunId, onSelectAgent }: {
  wave: AgentWave;
  selectedPhaseId?: string | null;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (wave.id === selectedPhaseId) ref.current?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }, [wave.id, selectedPhaseId]);

  // Single agent → its full terminal directly (no marker, no tile) — the de-dup.
  if (wave.agents.length === 1) {
    return <div className="run-tl-phase" ref={ref}><AgentTerminal agent={wave.agents[0]} /></div>;
  }

  const open = wave.agents.find((a) => a.agentRunId === selectedAgentRunId) ?? null;
  const toggle = (id: string) => onSelectAgent?.(id === selectedAgentRunId ? null : id);
  const breakdown = waveBreakdown(wave.agents);

  return (
    <div className="run-tl-phase" ref={ref}>
      <div className="run-tl-marker">
        <span className="run-tl-marker-name" title={wave.label}>{wave.label}</span>
        <span className="run-tl-dots" aria-hidden="true">
          {wave.agents.map((a) => <i key={a.agentRunId} data-state={tileState(a.status)} />)}
        </span>
        <span className="run-tl-marker-sub">{phaseSummary(breakdown)}</span>
      </div>

      <div className="run-tl-tiles">
        {wave.agents.map((a: PhaseAgentRef) => (
          <AgentTile key={a.agentRunId} agent={a} selected={a.agentRunId === selectedAgentRunId} open={a.agentRunId === selectedAgentRunId} onOpen={() => toggle(a.agentRunId)} />
        ))}
      </div>

      {open && <AgentTerminal agent={open} onClose={() => onSelectAgent?.(null)} />}
    </div>
  );
}

/** The marker's live summary — the in-flight counts while running ("2 running · 2 queued"), else the settled outcome. */
function phaseSummary(b: WaveBreakdown): string {
  if (b.running === 0 && b.queued === 0) return b.failed > 0 ? `${b.failed} failed · ${b.done} done` : "done";

  return [b.running > 0 && `${b.running} running`, b.queued > 0 && `${b.queued} queued`, b.failed > 0 && `${b.failed} failed`].filter(Boolean).join(" · ");
}
