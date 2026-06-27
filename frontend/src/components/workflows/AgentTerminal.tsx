import { useState, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentRunEventDto } from "@/api/agents";
import type { PhaseAgentRef } from "@/api/workflows";
import { useAgentRun, useAgentRunEvents } from "@/hooks/use-agents";

import { AgentToolCalls } from "./AgentToolCalls";
import { isAgentBusy } from "./runPhases";
import { formatDuration, formatTokens, formatUsd, tileState } from "./runActivity";

/**
 * The expanded agent terminal — opening an agent (a fleet dot, a table row, a tile) pops this full mac-terminal WINDOW
 * under the wave (ONE per wave). The title bar leads with the agent NAME; an identity strip carries harness · model ·
 * tool count · run time (the Slice-A rollup the ref already holds), so everything about the agent lives in one place.
 * The Output tab is the agent's whole event stream re-skinned as terminal scrollback (commands prompted, errors
 * toned); the Tool calls tab drills into the governed audit; the footer carries the live status + token + file rollup.
 * STILL read-only — there is no input; the window only reveals scrollback / tools, never a real shell. The full raw
 * ledger lives in the Trace tab.
 */
export function AgentTerminal({ agent, onClose, rerun }: { agent: PhaseAgentRef; onClose?: () => void; rerun?: ReactNode }) {
  const [tab, setTab] = useState<"output" | "tools">("output");

  const run = useAgentRun(agent.agentRunId);
  const status = run.data?.status ?? agent.status;
  const active = isAgentBusy(status);
  const events = useAgentRunEvents(agent.agentRunId, active);

  const name = agent.label || agent.nodeId || `agent ${agent.agentRunId.slice(0, 8)}`;
  const evts = events.data ?? [];
  const files = agent.filesChanged ?? 0;   // git-truth count off the ref (not a live FileChanged-event tally, which can double-count)
  const tokens = (agent.inputTokens ?? 0) + (agent.outputTokens ?? 0);
  const cost = agent.costUsd ?? 0;

  // The agent's identity strip — harness (the live run row) + model + tools + time (the phase rollup). Each part drops when absent.
  const identity = [
    run.data?.harness,
    agent.model,
    agent.toolCount != null && `${agent.toolCount} ${agent.toolCount === 1 ? "tool" : "tools"}`,
    agent.durationMs != null && formatDuration(agent.durationMs),
  ].filter(Boolean) as string[];

  return (
    <div className="agent-terminal" data-state={tileState(status)}>
      <div className="agent-terminal-bar">
        <span className="agent-terminal-lights" aria-hidden="true"><i></i><i></i><i></i></span>
        <span className="agent-terminal-title" title={name}>{name}</span>
        {onClose && <button type="button" className="agent-terminal-close" onClick={onClose} aria-label="Collapse terminal"><Ic.Collapse size={13} /></button>}
      </div>

      {identity.length > 0 && (
        <div className="agent-terminal-meta">
          {identity.map((part, i) => <span key={i}>{part}</span>)}
        </div>
      )}

      <div className="agent-terminal-body">
        {tab === "output" ? <Scrollback events={evts} loading={events.isLoading && evts.length === 0} error={tileState(status) === "failed" ? run.data?.error ?? null : null} /> : <AgentToolCalls agentRunId={agent.agentRunId} />}
      </div>

      <div className="agent-terminal-footer">
        <span className="agent-terminal-stat" data-state={tileState(status)}><span className="agent-terminal-statdot" aria-hidden="true"></span>{humanize(status)}</span>
        {rerun}
        {tokens > 0 && <span className="agent-terminal-fact">{formatTokens(tokens)} tokens</span>}
        {cost > 0 && <span className="agent-terminal-fact">{formatUsd(cost)}</span>}
        {files > 0 && <span className="agent-terminal-fact">{files} {files === 1 ? "file" : "files"}</span>}
        <div className="agent-terminal-tabs">
          <button type="button" data-active={tab === "output" || undefined} onClick={() => setTab("output")}>Output</button>
          <button type="button" data-active={tab === "tools" || undefined} onClick={() => setTab("tools")}>Tool calls</button>
        </div>
      </div>
    </div>
  );
}

function Scrollback({ events, loading, error }: { events: AgentRunEventDto[]; loading: boolean; error: string | null }) {
  if (events.length === 0) {
    // A run that failed BEFORE emitting any event (a dispatch / harness error) carries its reason on the run's `error`,
    // not in the empty stream — surface it as an error line so a failed terminal always says WHY, never "No output yet.".
    if (error) return <ol className="agent-terminal-scroll"><li className="agent-terminal-row" data-kind="error">{error}</li></ol>;
    return <div className="agent-terminal-empty">{loading ? "Connecting to the sandbox…" : "No output yet."}</div>;
  }

  return (
    <ol className="agent-terminal-scroll">
      {events.map((e) => (
        <li key={e.sequence} className="agent-terminal-row" data-kind={lineKind(e.kind)}>
          {isCommand(e.kind) && <span className="agent-terminal-prompt">❯ </span>}{e.text || humanize(e.kind)}
        </li>
      ))}
    </ol>
  );
}

/** "TimedOut" → "timed out", "FinalSummary" → "final summary" — split camelCase + lowercase for a micro-label. */
function humanize(s: string): string {
  return s.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
}

/** A command-shaped event gets the prompt; the rest are output lines. */
function isCommand(kind: string): boolean {
  return kind === "CommandExecuted" || kind === "ToolCall";
}

/** The line's tone — errors danger, warnings amber, commands accented, everything else plain output. */
function lineKind(kind: string): "error" | "warn" | "command" | "out" {
  if (kind === "Error") return "error";
  if (kind === "Warning") return "warn";
  if (isCommand(kind)) return "command";
  return "out";
}
