import type { PhaseAgentRef } from "@/api/workflows";

import { formatDuration, formatTokens, tileState } from "./runActivity";

/**
 * The wave's COLLAPSED density — its agents as a compact, Claude-Code-style table (Agent · Tokens · Tools · Time). It
 * is PURE: every column reads off the phase ref's backend rollup, so the default view of a wave costs ZERO per-agent
 * fetches — the live terminal previews only load when the operator expands to tiles. A row is a button into that
 * agent's terminal (the wave renders it below, ONE per wave); a done row dims, the selected / open row rings.
 */
export function AgentWaveTable({ agents, selectedAgentRunId, openId, onOpen }: { agents: PhaseAgentRef[]; selectedAgentRunId?: string | null; openId: string | null; onOpen: (id: string) => void }) {
  return (
    <table className="agent-wave-table">
      <thead>
        <tr>
          <th scope="col">Agent</th>
          <th scope="col" className="agent-wave-num">Tokens</th>
          <th scope="col" className="agent-wave-num">Tools</th>
          <th scope="col" className="agent-wave-num">Time</th>
        </tr>
      </thead>
      <tbody>
        {agents.map((a) => (
          <AgentRow key={a.agentRunId} agent={a} selected={a.agentRunId === selectedAgentRunId} open={a.agentRunId === openId} onOpen={() => onOpen(a.agentRunId)} />
        ))}
      </tbody>
    </table>
  );
}

function AgentRow({ agent, selected, open, onOpen }: { agent: PhaseAgentRef; selected: boolean; open: boolean; onOpen: () => void }) {
  const state = tileState(agent.status);
  const name = agent.label || agent.nodeId || `agent ${agent.agentRunId.slice(0, 8)}`;
  const tokens = (agent.inputTokens ?? 0) + (agent.outputTokens ?? 0);

  // The row stays a REAL table row (cells associated with their column headers); the open affordance is a real button
  // on the name cell — keyboard/SR-accessible, and a screen reader still reads each Tokens/Tools/Time cell with its header.
  return (
    <tr className="agent-wave-row" data-state={state} data-selected={selected || undefined} data-open={open || undefined}>
      <td className="agent-wave-cell-name">
        <span className="agent-wave-dot" data-state={state} aria-hidden="true"></span>
        <button type="button" className="agent-wave-name" aria-expanded={open} onClick={onOpen} title={name}>{name}</button>
        {agent.model && <span className="agent-wave-model" title={agent.model}>{agent.model}</span>}
      </td>
      <td className="agent-wave-num">{tokens > 0 ? formatTokens(tokens) : "—"}</td>
      <td className="agent-wave-num">{agent.toolCount ?? "—"}</td>
      <td className="agent-wave-num">{formatDuration(agent.durationMs)}</td>
    </tr>
  );
}
