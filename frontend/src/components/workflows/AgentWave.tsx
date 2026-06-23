import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

import { AgentFleetDots } from "./AgentFleetDots";
import { AgentTerminal } from "./AgentTerminal";
import { AgentTile } from "./AgentTile";
import { AgentWaveTable } from "./AgentWaveTable";
import { formatBreakdown, waveBreakdown, type AgentWave as AgentWaveModel } from "./runActivity";

/**
 * An agent wave at THREE densities, one toggle. The DEFAULT is the FLEET CARD — a quiet header (icon + phase label +
 * a one-line breakdown like "8 agents · 4 done · 2 running") over a row of status DOTS, one per agent (pending gray ·
 * running blue · done green · failed red), each a button into that agent's terminal. The header chevron EXPANDS to the
 * metrics table (with a tiles sub-toggle for the live mac-terminal previews). From ANY density, opening an agent pops
 * its full TERMINAL below the wave — ONE open per wave (re-clicking the same agent collapses it). Warm-light
 * throughout; only the tiles + the terminal carry terminal chrome.
 */
export function AgentWave({ wave, selectedAgentRunId }: { wave: AgentWaveModel; selectedAgentRunId?: string | null }) {
  const [expanded, setExpanded] = useState(false);
  const [tiles, setTiles] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);

  // The one expanded agent's terminal (if any). `find` also guards a stale id if the wave's agent set changes under us.
  const openAgent = wave.agents.find((a) => a.agentRunId === openId) ?? null;
  const toggleOpen = (id: string) => setOpenId((cur) => (cur === id ? null : id));

  const summary = formatBreakdown(waveBreakdown(wave.agents));

  return (
    <section className="agent-wave" aria-label={`Agent wave: ${wave.label}`} data-expanded={expanded || undefined}>
      <div className="agent-wave-head">
        <Ic.Bot size={14} aria-hidden="true" />
        <span className="agent-wave-title" title={wave.label}>{wave.label}</span>
        <span className="agent-wave-summary">{summary}</span>
        <button
          type="button"
          className="agent-wave-toggle"
          data-open={expanded || undefined}
          onClick={() => setExpanded((v) => !v)}
          aria-expanded={expanded}
          aria-label="Agent detail"
        >
          <Ic.ChevronDown size={14} />
        </button>
      </div>

      <AgentFleetDots agents={wave.agents} selectedAgentRunId={selectedAgentRunId} openId={openId} onOpen={toggleOpen} />

      {expanded && (
        <div className="agent-wave-body">
          <div className="agent-wave-modes" role="group" aria-label="Detail view">
            <button type="button" data-active={!tiles || undefined} aria-pressed={!tiles} onClick={() => setTiles(false)}>Table</button>
            <button type="button" data-active={tiles || undefined} aria-pressed={tiles} onClick={() => setTiles(true)}>Tiles</button>
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
        </div>
      )}

      {openAgent && <AgentTerminal agent={openAgent} onClose={() => setOpenId(null)} />}
    </section>
  );
}
