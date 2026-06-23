import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

import { AgentTerminal } from "./AgentTerminal";
import { AgentTile } from "./AgentTile";
import type { AgentWave as AgentWaveModel } from "./runActivity";

/**
 * An agent wave — the agents a phase spawned, rendered as an ADAPTIVE grid of light terminal tiles under a quiet
 * wave header. The grid auto-fills (3–4 tiles a row on desktop, wraps below, single column on mobile), so a wave of
 * one and a wave of eight read the same way. Clicking a tile opens its full terminal below the grid — ONE open per
 * wave (clicking the open tile collapses it), so the page never explodes. The header stays warm-light; only the
 * tiles + the expanded terminal carry the terminal chrome.
 */
export function AgentWave({ wave, selectedAgentRunId }: { wave: AgentWaveModel; selectedAgentRunId?: string | null }) {
  const n = wave.agents.length;
  const [openId, setOpenId] = useState<string | null>(null);

  // The one expanded tile (if any). `find` also guards a stale id if the wave's agent set changes under us.
  const openAgent = wave.agents.find((a) => a.agentRunId === openId) ?? null;

  return (
    <section className="agent-wave" aria-label={`Agent wave: ${wave.label}`}>
      <div className="agent-wave-head">
        <Ic.Bot size={14} aria-hidden="true" />
        <span className="agent-wave-title" title={wave.label}>{wave.label}</span>
        <span className="agent-wave-count">{n} {n === 1 ? "agent" : "agents"}</span>
      </div>

      <div className="agent-wave-grid">
        {wave.agents.map((a) => (
          <AgentTile
            key={a.agentRunId}
            agent={a}
            selected={a.agentRunId === selectedAgentRunId}
            open={a.agentRunId === openId}
            onOpen={() => setOpenId((cur) => (cur === a.agentRunId ? null : a.agentRunId))}
          />
        ))}
      </div>

      {openAgent && <AgentTerminal agent={openAgent} onClose={() => setOpenId(null)} />}
    </section>
  );
}
