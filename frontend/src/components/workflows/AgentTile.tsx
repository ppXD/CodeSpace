import { useEffect, useRef } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PhaseAgentRef } from "@/api/workflows";
import { useAgentRun, useAgentRunEvents } from "@/hooks/use-agents";

import { isAgentBusy } from "./runPhases";
import { formatTokens, tileState, type TileState } from "./runActivity";

/**
 * One agent rendered as a light "terminal tile" — the live-execution unit of an agent wave. It is a READ-ONLY log
 * preview dressed in mac-terminal chrome (traffic lights + a name title), NOT an interactive shell: a running agent
 * shows its latest line + a blinking cursor, a finished one dims and keeps its output summary, a queued one reads gray,
 * a failed one reads danger. Stays to TWO preview lines so a wave of many tiles reads at a glance. Clicking the tile
 * (when `onOpen` is given) opens its full terminal scrollback below the wave; `open` marks the one that's expanded.
 * Status is the agent run's LIVE status, falling back to the phase ref's.
 */
export function AgentTile({ agent, selected, open, onOpen }: { agent: PhaseAgentRef; selected?: boolean; open?: boolean; onOpen?: () => void }) {
  const tileRef = useRef<HTMLDivElement>(null);

  // When the outline selects this agent, bring its tile into view (within the Activity panel's own scroll).
  useEffect(() => {
    if (selected) tileRef.current?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }, [selected]);

  const run = useAgentRun(agent.agentRunId);
  const status = run.data?.status ?? agent.status;
  const active = isAgentBusy(status);
  const events = useAgentRunEvents(agent.agentRunId, active);

  const name = agent.label || agent.nodeId || `agent ${agent.agentRunId.slice(0, 8)}`;
  const state = tileState(status);

  const evts = events.data ?? [];
  const latest = evts.length > 0 ? evts[evts.length - 1].text : undefined;
  const files = evts.filter((e) => e.kind === "FileChanged").length;
  const tokens = (agent.inputTokens ?? 0) + (agent.outputTokens ?? 0);
  const summary = metricLine(files, tokens);
  const failReason = run.data?.error || undefined;   // a fail-before-events run has no `latest`; its reason lives on the run (|| so an empty error falls through to "stopped")

  return (
    <div
      className="agent-tile"
      role={onOpen ? "button" : undefined}
      tabIndex={onOpen ? 0 : undefined}
      data-state={state}
      data-selected={selected || undefined}
      data-open={open || undefined}
      aria-expanded={onOpen ? !!open : undefined}
      onClick={onOpen}
      onKeyDown={onOpen ? (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(); } } : undefined}
      ref={tileRef}
    >
      <div className="agent-tile-bar">
        <span className="agent-tile-lights" aria-hidden="true"><i></i><i></i><i></i></span>
        <span className="agent-tile-name" title={name}>{name}</span>
        <span className="agent-tile-flag" aria-hidden="true">{stateFlag(state)}</span>
      </div>
      <div className="agent-tile-body">{bodyLines(state, latest, summary, failReason)}</div>
    </div>
  );
}

/** The two-line preview body, per state — a running agent gets its live line + cursor; the others a quiet two-liner. */
function bodyLines(state: TileState, latest: string | undefined, summary: string, failReason: string | undefined) {
  if (state === "running") {
    return (
      <>
        <div className="agent-tile-line agent-tile-cmd"><span className="agent-tile-prompt">❯</span> {latest ?? "working…"}</div>
        <div className="agent-tile-line agent-tile-dim">{summary || "running"}<span className="agent-tile-cursor" aria-hidden="true"></span></div>
      </>
    );
  }

  if (state === "waiting") {
    return (
      <>
        <div className="agent-tile-line agent-tile-wait">waiting</div>
        <div className="agent-tile-line agent-tile-dim">queued · no changes yet</div>
      </>
    );
  }

  if (state === "failed") {
    return (
      <>
        <div className="agent-tile-line agent-tile-cmd">{latest ?? failReason ?? "stopped"}</div>
        <div className="agent-tile-line agent-tile-fail">failed</div>
      </>
    );
  }

  // done — dim, but keep the output summary.
  return (
    <>
      <div className="agent-tile-line">{summary || "completed"}</div>
      <div className="agent-tile-line agent-tile-dim">done</div>
    </>
  );
}

function stateFlag(state: TileState) {
  if (state === "running") return <span className="agent-tile-live"></span>;
  if (state === "waiting") return <Ic.Clock size={12} />;
  if (state === "failed") return <Ic.X size={13} />;
  return <Ic.Check size={13} />;
}

/** "2 files · 14.2k tokens" — each part dropped when zero; "" when the agent produced neither yet. */
function metricLine(files: number, tokens: number): string {
  return [files > 0 && `${files} ${files === 1 ? "file" : "files"}`, tokens > 0 && `${formatTokens(tokens)} tokens`].filter(Boolean).join(" · ");
}
