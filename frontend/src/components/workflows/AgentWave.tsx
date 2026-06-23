import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

import { AgentTerminal } from "./AgentTerminal";
import { AgentTile } from "./AgentTile";
import { AgentWaveTable } from "./AgentWaveTable";
import { waveSummary, type AgentWave as AgentWaveModel } from "./runActivity";

/**
 * An agent wave — the agents a phase spawned, at THREE densities driven by one toggle. The DEFAULT is the collapsed
 * compact table (Agent · Tokens · Tools · Time, cheap — no per-agent fetch); the header chevron expands it to the
 * adaptive grid of live terminal TILES (each polling its own log preview). From either density, clicking an agent
 * opens its full TERMINAL below the wave — ONE open per wave (re-clicking the same agent collapses it), so the page
 * never explodes. The header stays warm-light; only the tiles + the expanded terminal carry the terminal chrome.
 */
export function AgentWave({ wave, selectedAgentRunId }: { wave: AgentWaveModel; selectedAgentRunId?: string | null }) {
  const n = wave.agents.length;
  const [tiles, setTiles] = useState(false);   // false = collapsed table (default), true = terminal tiles
  const [openId, setOpenId] = useState<string | null>(null);

  // The one expanded agent's terminal (if any). `find` also guards a stale id if the wave's agent set changes under us.
  const openAgent = wave.agents.find((a) => a.agentRunId === openId) ?? null;
  const toggleOpen = (id: string) => setOpenId((cur) => (cur === id ? null : id));

  const { done, total, state } = waveSummary(wave.agents);

  return (
    <section className="agent-wave" aria-label={`Agent wave: ${wave.label}`} data-density={tiles ? "tiles" : "table"}>
      <div className="agent-wave-head">
        <Ic.Bot size={14} aria-hidden="true" />
        <span className="agent-wave-title" title={wave.label}>{wave.label}</span>
        <WaveProgress done={done} total={total} />
        <span className="agent-wave-count">{n} {n === 1 ? "agent" : "agents"} · <span className="agent-wave-state" data-state={state}>{state}</span></span>
        <button
          type="button"
          className="agent-wave-toggle"
          data-open={tiles || undefined}
          onClick={() => setTiles((v) => !v)}
          aria-pressed={tiles}
          aria-label="Terminal tiles"
        >
          <Ic.ChevronDown size={14} />
        </button>
      </div>

      {tiles ? (
        <div className="agent-wave-grid">
          {wave.agents.map((a) => (
            <AgentTile
              key={a.agentRunId}
              agent={a}
              selected={a.agentRunId === selectedAgentRunId}
              open={a.agentRunId === openId}
              onOpen={() => toggleOpen(a.agentRunId)}
            />
          ))}
        </div>
      ) : (
        <AgentWaveTable agents={wave.agents} selectedAgentRunId={selectedAgentRunId} openId={openId} onOpen={toggleOpen} />
      )}

      {openAgent && <AgentTerminal agent={openAgent} onClose={() => setOpenId(null)} />}
    </section>
  );
}

/** The wave's progress — small filled / outline squares (done of total) for a small wave, a "done/total" count once a wave grows past eight so the squares never crowd the header. */
function WaveProgress({ done, total }: { done: number; total: number }) {
  if (total > 8) return <span className="agent-wave-progress-text" aria-hidden="true">{done}/{total}</span>;

  return (
    <span className="agent-wave-progress" aria-hidden="true">
      {Array.from({ length: total }).map((_, i) => <i key={i} data-on={i < done || undefined} />)}
    </span>
  );
}
