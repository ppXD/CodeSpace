import { Ic } from "@/_imported/ai-code-space/icons";

import { AgentTile } from "./AgentTile";
import type { AgentWave as AgentWaveModel } from "./runActivity";

/**
 * An agent wave — the agents a phase spawned, rendered as an ADAPTIVE grid of light terminal tiles under a quiet
 * wave header. The grid auto-fills (3–4 tiles a row on desktop, wraps below, single column on mobile), so a wave of
 * one and a wave of eight read the same way — you scan who's running and what each is doing at a glance. The header
 * (wave label + agent count) stays in the warm light theme; only the tiles carry the terminal chrome.
 */
export function AgentWave({ wave, selectedAgentRunId }: { wave: AgentWaveModel; selectedAgentRunId?: string | null }) {
  const n = wave.agents.length;

  return (
    <section className="agent-wave" aria-label={`Agent wave: ${wave.label}`}>
      <div className="agent-wave-head">
        <Ic.Bot size={14} aria-hidden="true" />
        <span className="agent-wave-title" title={wave.label}>{wave.label}</span>
        <span className="agent-wave-count">{n} {n === 1 ? "agent" : "agents"}</span>
      </div>

      <div className="agent-wave-grid">
        {wave.agents.map((a) => <AgentTile key={a.agentRunId} agent={a} selected={a.agentRunId === selectedAgentRunId} />)}
      </div>
    </section>
  );
}
