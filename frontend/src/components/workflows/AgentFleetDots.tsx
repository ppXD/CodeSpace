import type { PhaseAgentRef } from "@/api/workflows";

import { tileState } from "./runActivity";

/**
 * The fleet dots — the wave's second header row, one small status dot per agent (the Claude-Code "box of agents"
 * read): pending gray · running blue · done green · failed red. Pure (reads the phase refs, no per-agent fetch). Each
 * dot is a real button into that agent's terminal; the outline-selected or open agent's dot rings. It wraps, so a wave
 * of one and a wave of twenty both read at a glance.
 */
export function AgentFleetDots({ agents, selectedAgentRunId, openId, onOpen }: { agents: PhaseAgentRef[]; selectedAgentRunId?: string | null; openId: string | null; onOpen: (id: string) => void }) {
  return (
    <div className="agent-fleet" role="group" aria-label="Agents">
      {agents.map((a) => {
        const name = a.label || a.nodeId || `agent ${a.agentRunId.slice(0, 8)}`;
        const state = tileState(a.status);

        return (
          <button
            key={a.agentRunId}
            type="button"
            className="agent-fleet-dot"
            data-state={state}
            data-selected={a.agentRunId === selectedAgentRunId || undefined}
            data-open={a.agentRunId === openId || undefined}
            aria-expanded={a.agentRunId === openId}
            aria-label={`${name} — ${state}`}
            title={`${name} · ${state}`}
            onClick={() => onOpen(a.agentRunId)}
          />
        );
      })}
    </div>
  );
}
