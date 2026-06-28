import { memo, useEffect, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PhaseAgentRef } from "@/api/workflows";

import { AgentTerminal } from "./AgentTerminal";
import { AgentTile } from "./AgentTile";
import { NodeRerunBadge } from "./NodeRerunBadge";
import { formatDuration, itemRerunTarget, phaseRerunTarget, tileState, waveBreakdown, type AgentWave, type WaveBreakdown } from "./runActivity";
import { RerunMenu } from "./RerunMenu";

/**
 * One phase on the Activity timeline as a Claude-style CONVERSATION BOX — a 3-level drill-down. Collapsed it's a
 * compact summary: phase name + "N agents · time" + a status dot per agent. Click to expand: a MULTI-agent phase
 * reveals its terminal-tile thumbnails, and clicking a tile opens that one agent's full real-time terminal below; a
 * SINGLE-agent phase skips the tile layer and opens straight to its one terminal. The box defaults open while the
 * phase is in flight (else collapsed, so a long run reads as a tidy stack of summaries) and the outline force-opens it
 * when it selects one of its agents (that terminal must show, outranking a stale manual collapse). The open terminal is
 * keyed off `selectedAgentRunId` (lifted to the route) so the outline + timeline share one selection. Scrolls into view
 * when the outline selects this phase.
 */
function TimelinePhaseImpl({ wave, selectedPhaseId, selectedAgentRunId, onSelectAgent, rerunnableNodeIds }: {
  wave: AgentWave;
  selectedPhaseId?: string | null;
  selectedAgentRunId?: string | null;
  onSelectAgent?: (agentRunId: string | null) => void;
  rerunnableNodeIds?: ReadonlySet<string>;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const breakdown = waveBreakdown(wave.agents);
  const isActive = breakdown.running > 0 || breakdown.queued > 0;
  const containsSelected = wave.agents.some((a) => a.agentRunId === selectedAgentRunId);

  // Open is DERIVED. Selecting an agent in this phase from the outline force-opens it (the picked terminal must be
  // visible) and outranks a stale manual toggle; otherwise a manual toggle wins once set, else the box defaults open
  // only while the phase is in flight — so a settled run reads as a tidy stack of collapsed summaries.
  const [userToggle, setUserToggle] = useState<boolean | null>(null);
  const open = containsSelected ? true : (userToggle ?? isActive);

  useEffect(() => {
    if (wave.id === selectedPhaseId) ref.current?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  }, [wave.id, selectedPhaseId]);

  const single = wave.agents.length === 1;
  const openAgent = wave.agents.find((a) => a.agentRunId === selectedAgentRunId) ?? null;
  const toggleAgent = (id: string) => onSelectAgent?.(id === selectedAgentRunId ? null : id);

  // EVERY phase box carries its rerun on its own bottom row, right below the status dots — a map fan-out gets the bulk
  // "Rerun N failed items", a single-agent / node phase gets "Rerun from here". phaseRerunTarget returns null for a
  // clean phase, so the row only appears where there's something to rerun. The expanded terminal keeps its own copy.
  const headerTarget = phaseRerunTarget(wave, rerunnableNodeIds);
  const terminalRerun = (agent: PhaseAgentRef) => {
    const t = wave.kind === "map" ? itemRerunTarget(agent, wave) : phaseRerunTarget(wave, rerunnableNodeIds);
    return t ? <RerunMenu target={t} className="run-tl-rerun-term" /> : null;
  };
  // A per-tile compact rerun on each fanned-out item, so the small terminals carry their own "Rerun item" too.
  const tileRerun = (agent: PhaseAgentRef) => {
    const t = wave.kind === "map" ? itemRerunTarget(agent, wave) : null;
    return t ? <RerunMenu target={t} compact bare className="run-tl-rerun-tile" /> : null;
  };

  return (
    <div className="run-tl-phase" ref={ref}>
      {/* The box is a role=button DIV (not a native <button>) so the rerun can live INSIDE it as a real child button
          on its own row below the dots — a native button can't nest a button. Clicking the rerun stops propagation so
          it never toggles the box. */}
      <div
        className="run-tl-box"
        role="button"
        tabIndex={0}
        data-open={open || undefined}
        aria-expanded={open}
        onClick={() => setUserToggle(!open)}
        onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setUserToggle(!open); } }}
      >
        <span className="run-tl-box-head">
          <span className="run-tl-box-name" title={wave.label}>{wave.label}</span>
          <NodeRerunBadge nodeId={wave.id} className="run-tl-box-rerun" />
          <Ic.ChevronRight className="run-tl-box-caret" size={14} aria-hidden="true" />
        </span>
        <span className="run-tl-box-meta">{boxMeta(wave, breakdown)}</span>
        <span className="run-tl-dots" aria-hidden="true">
          {wave.agents.map((a) => <i key={a.agentRunId} data-state={tileState(a.status)} />)}
        </span>
        {headerTarget && (
          <div className="run-tl-rerun-row" onClick={(e) => e.stopPropagation()} onKeyDown={(e) => e.stopPropagation()} role="presentation">
            <RerunMenu target={headerTarget} compact className="run-tl-rerun" />
          </div>
        )}
      </div>

      {open && (single
        // Single agent → its full terminal directly (skip the tile layer); close collapses the box AND clears the
        // shared selection (else the outline row stays highlighted, and a selection-driven open would re-open it).
        ? <AgentTerminal agent={wave.agents[0]} onClose={() => { setUserToggle(false); onSelectAgent?.(null); }} rerun={terminalRerun(wave.agents[0])} />
        : (
          <>
            <div className="run-tl-tiles">
              {wave.agents.map((a: PhaseAgentRef) => (
                <AgentTile key={a.agentRunId} agent={a} selected={a.agentRunId === selectedAgentRunId} open={a.agentRunId === selectedAgentRunId} onOpen={() => toggleAgent(a.agentRunId)} rerun={tileRerun(a)} />
              ))}
            </div>

            {openAgent && <AgentTerminal key={openAgent.agentRunId} agent={openAgent} onClose={() => onSelectAgent?.(null)} rerun={terminalRerun(openAgent)} />}
          </>
        ))}
    </div>
  );
}

/**
 * Memoized: a phase only re-renders when its OWN inputs change. The Activity timeline polls events + phases on
 * separate 2s cadences; an events-only tick rebuilds the item list but reuses the (memoized) wave objects, so without
 * this every phase box + its tiles re-render each tick. Props are referentially stable — `wave` survives an
 * events-only poll (memoized in RunActivityTimeline), `onSelectAgent` is the route's state setter, and selection ids
 * change only on a click — so the memo confines re-renders to the phases that actually changed.
 */
export const TimelinePhase = memo(TimelinePhaseImpl);

/** The box's one-line meta — "{N} agents · {what}": the in-flight counts while running, the wall-clock when settled, the failure count on error. */
function boxMeta(wave: AgentWave, b: WaveBreakdown): string {
  const agents = `${b.total} agent${b.total === 1 ? "" : "s"}`;

  if (b.running > 0 || b.queued > 0) {
    const live = [b.running > 0 && `${b.running} running`, b.queued > 0 && `${b.queued} queued`].filter(Boolean).join(" · ");
    return `${agents} · ${live}`;
  }

  if (b.failed > 0) return `${agents} · ${b.failed} failed`;

  return `${agents} · ${formatDuration(maxDuration(wave.agents))}`;
}

/** The phase wall-clock ≈ the longest-running agent; null when no agent reported a duration (→ "—"). */
function maxDuration(agents: readonly PhaseAgentRef[]): number | null {
  const ds = agents.map((a) => a.durationMs).filter((d): d is number => d != null);
  return ds.length ? Math.max(...ds) : null;
}
