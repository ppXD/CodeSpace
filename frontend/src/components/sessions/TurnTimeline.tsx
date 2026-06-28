import { memo, useMemo, useState } from "react";

import type { PhaseAgentRef, WorkflowRunStatus } from "@/api/workflows";
import { AgentTerminal } from "@/components/workflows/AgentTerminal";
import { AgentTile } from "@/components/workflows/AgentTile";
import { DecisionCard } from "@/components/workflows/DecisionCard";
import { NodeRerunBadge } from "@/components/workflows/NodeRerunBadge";
import { RerunMenu } from "@/components/workflows/RerunMenu";
import { RunActionsContext } from "@/components/workflows/runActionsContext";
import { RunOpenContext } from "@/components/workflows/runOpenContext";
import { RerunProvenanceContext } from "@/components/workflows/rerunProvenanceContext";
import { buildWaves, formatDuration, itemRerunTarget, phaseRerunTarget, waveBreakdown, type AgentWave, type WaveBreakdown } from "@/components/workflows/runActivity";
import { decisionsForRun } from "@/components/workflows/runDecisions";
import { rerunsByNode } from "@/components/workflows/runRerunProvenance";
import { summarizeRunState } from "@/components/workflows/runPhases";
import { isRunActive, useRunAttempts, usePendingDecisions, useRunPhases, useWorkflowRun } from "@/hooks/use-workflows";

/**
 * One turn rendered as an AI reply: a humanized header (what CodeSpace is doing, first-person) on top, then the run's
 * phases as a STREAMING STEP TIMELINE — each step a gutter glyph + a plain-language line; a fan-out step expands into
 * the live agent terminal tiles, a parked decision shows an inline answerable card, and reruns anchor per step (only
 * where the backend accepts). Replaces the raw React Flow canvas inside a turn. Driven entirely by the phase projection
 * (polled 2s), so it advances as the run advances — like watching an agent work.
 */
export function TurnTimeline({ runId, onOpenRun }: { runId: string; onOpenRun: (runId: string) => void }) {
  const phases = useRunPhases(runId);
  const run = useWorkflowRun(runId);
  const attempts = useRunAttempts(runId);

  const active = run.data ? isRunActive(run.data.status) : true;
  const decisions = usePendingDecisions(active);

  const phaseList = phases.data?.phases ?? [];
  const waves = useMemo(() => buildWaves(phaseList), [phaseList]);
  const runStatus: WorkflowRunStatus = run.data?.status ?? "Running";
  const state = summarizeRunState(runStatus, phaseList);

  const rerunnableNodeIds = useMemo(() => new Set((run.data?.nodes ?? []).filter((n) => n.rerunnableFromHere).map((n) => n.nodeId)), [run.data?.nodes]);
  const ladder = attempts.data?.attempts ?? [];
  const provKey = ladder.map((a) => `${a.runId}:${a.status}`).join("|");
  // eslint-disable-next-line react-hooks/exhaustive-deps -- provKey is the content digest of the ladder
  const provenance = useMemo(() => ({ attempts: ladder, rerunsByNode: rerunsByNode(ladder) }), [provKey]);

  const runAgentIds = new Set(phaseList.flatMap((p) => p.agents).map((a) => a.agentRunId));
  const runDecisions = decisions.data ? decisionsForRun(decisions.data, runId, runAgentIds) : [];

  const [selectedAgentRunId, setSelectedAgentRunId] = useState<string | null>(null);

  if (phases.isLoading && phaseList.length === 0) {
    return <div className="turn-tl-empty">Loading the run…</div>;
  }

  return (
    <RunActionsContext.Provider value={{ runId, isTerminal: !active }}>
      <RunOpenContext.Provider value={onOpenRun}>
        <RerunProvenanceContext.Provider value={provenance}>
          <div className="turn-tl">
            <TurnHeader status={runStatus} focus={state.focus} activeAgents={state.activeAgents} totalAgents={state.totalAgents} waiting={state.waiting} />

            <div className="turn-tl-steps">
              {waves.map((wave, i) => (
                <TimelineStep
                  key={wave.id}
                  wave={wave}
                  last={i === waves.length - 1 && runDecisions.length === 0}
                  rerunnableNodeIds={rerunnableNodeIds}
                  selectedAgentRunId={selectedAgentRunId}
                  onSelectAgent={setSelectedAgentRunId}
                />
              ))}

              {runDecisions.map((d, i) => (
                <div className="turn-tl-step" key={d.id}>
                  <span className="turn-tl-gut"><span className="turn-tl-gl turn-tl-gl-wait" aria-hidden="true">!</span>{i < runDecisions.length - 1 && <span className="turn-tl-line" />}</span>
                  <div className="turn-tl-body"><DecisionCard decision={d} /></div>
                </div>
              ))}

              {waves.length === 0 && runDecisions.length === 0 && <div className="turn-tl-empty">No steps yet.</div>}
            </div>
          </div>
        </RerunProvenanceContext.Provider>
      </RunOpenContext.Provider>
    </RunActionsContext.Provider>
  );
}

/** The first-person status line — "what CodeSpace is doing right now", derived from the run state. */
function TurnHeader({ status, focus, activeAgents, totalAgents, waiting }: { status: WorkflowRunStatus; focus: string; activeAgents: number; totalAgents: number; waiting: number }) {
  const live = isRunActive(status);
  const tone = status === "Failure" ? "err" : status === "Success" ? "ok" : status === "Suspended" ? "wait" : "run";

  const phrase = (() => {
    if (status === "Success") return "完成了。";
    if (status === "Failure") return "这一轮失败了。";
    if (status === "Cancelled") return "已取消。";
    const lead = focus ? `正在${focus}` : "正在处理";
    const agents = totalAgents > 0 ? ` · ${activeAgents}/${totalAgents} 个 agent 在跑` : "";
    const need = waiting > 0 ? ` · ${waiting} 处需要你` : "";
    return `${lead}${agents}${need}${live ? "…" : ""}`;
  })();

  return (
    <div className="turn-tl-head">
      <span className="turn-tl-av" aria-hidden="true">
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round"><path d="M12 5a3 3 0 0 0-3 3 3 3 0 0 0-1 5.8V16a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2v-2.2A3 3 0 0 0 15 8a3 3 0 0 0-3-3z" /><path d="M9 18v1M15 18v1" /></svg>
      </span>
      <span className="turn-tl-head-name">CodeSpace</span>
      <span className={`turn-tl-head-dot turn-tl-head-dot-${tone}`} aria-hidden="true" />
      <span className="turn-tl-head-phrase">{phrase}</span>
    </div>
  );
}

/** One phase as a streaming step: gutter glyph + a plain line, expanding into the live agent tiles / terminal. */
const TimelineStep = memo(function TimelineStep({ wave, last, rerunnableNodeIds, selectedAgentRunId, onSelectAgent }: {
  wave: AgentWave;
  last: boolean;
  rerunnableNodeIds: ReadonlySet<string>;
  selectedAgentRunId: string | null;
  onSelectAgent: (id: string | null) => void;
}) {
  const b = waveBreakdown(wave.agents);
  const isActive = b.running > 0 || b.queued > 0;
  const tone = b.failed > 0 ? "err" : isActive ? "run" : b.total > 0 ? "ok" : wave.startedAt ? "ok" : "pending";

  const containsSelected = wave.agents.some((a) => a.agentRunId === selectedAgentRunId);
  const [userToggle, setUserToggle] = useState<boolean | null>(null);
  const open = containsSelected ? true : (userToggle ?? isActive);

  const single = wave.agents.length === 1;
  const openAgent = wave.agents.find((a) => a.agentRunId === selectedAgentRunId) ?? null;
  const toggleAgent = (id: string) => onSelectAgent(id === selectedAgentRunId ? null : id);

  const headerTarget = phaseRerunTarget(wave, rerunnableNodeIds);
  const terminalRerun = (agent: PhaseAgentRef) => {
    const t = wave.kind === "map" ? itemRerunTarget(agent, wave) : phaseRerunTarget(wave, rerunnableNodeIds);
    return t ? <RerunMenu target={t} className="run-tl-rerun-term" /> : null;
  };
  const tileRerun = (agent: PhaseAgentRef) => {
    const t = wave.kind === "map" ? itemRerunTarget(agent, wave) : null;
    return t ? <RerunMenu target={t} compact bare className="run-tl-rerun-tile" /> : null;
  };

  return (
    <div className="turn-tl-step">
      <span className="turn-tl-gut">
        <span className={`turn-tl-gl turn-tl-gl-${tone}`} aria-hidden="true">{glyph(tone)}</span>
        {!last && <span className="turn-tl-line" />}
      </span>

      <div className="turn-tl-body">
        <div className="turn-tl-row" role="button" tabIndex={0} aria-expanded={open}
          onClick={() => setUserToggle(!open)}
          onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); setUserToggle(!open); } }}>
          <span className="turn-tl-label">{wave.label}{tone === "run" ? "…" : ""}</span>
          <span className="turn-tl-meta">{stepMeta(wave, b)}</span>
          <NodeRerunBadge nodeId={wave.id} className="turn-tl-rerunbadge" />
          {wave.agents.length > 0 && <span className="turn-tl-caret" data-open={open || undefined} aria-hidden="true">›</span>}
        </div>

        {headerTarget && (
          <div className="turn-tl-rerunrow" onClick={(e) => e.stopPropagation()} role="presentation">
            <RerunMenu target={headerTarget} compact className="run-tl-rerun" />
          </div>
        )}

        {open && wave.agents.length > 0 && (single
          ? <div className="turn-tl-termwrap"><AgentTerminal agent={wave.agents[0]} onClose={() => { setUserToggle(false); onSelectAgent(null); }} rerun={terminalRerun(wave.agents[0])} /></div>
          : (
            <>
              <div className="run-tl-tiles turn-tl-tiles">
                {wave.agents.map((a) => (
                  <AgentTile key={a.agentRunId} agent={a} selected={a.agentRunId === selectedAgentRunId} open={a.agentRunId === selectedAgentRunId} onOpen={() => toggleAgent(a.agentRunId)} rerun={tileRerun(a)} />
                ))}
              </div>
              {openAgent && <div className="turn-tl-termwrap"><AgentTerminal key={openAgent.agentRunId} agent={openAgent} onClose={() => onSelectAgent(null)} rerun={terminalRerun(openAgent)} /></div>}
            </>
          ))}
      </div>
    </div>
  );
});

function glyph(tone: string): string {
  if (tone === "ok") return "✓";
  if (tone === "err") return "✕";
  if (tone === "run") return "●";
  return "○";
}

/** The step's one-line meta — live counts while running, wall-clock when settled, failures on error. */
function stepMeta(wave: AgentWave, b: WaveBreakdown): string {
  if (b.total === 0) return "";
  if (b.running > 0 || b.queued > 0) {
    return [b.running > 0 && `${b.running} running`, b.queued > 0 && `${b.queued} queued`].filter(Boolean).join(" · ");
  }
  if (b.failed > 0) return `${b.failed} failed`;
  return formatDuration(maxDuration(wave.agents));
}

function maxDuration(agents: readonly PhaseAgentRef[]): number | null {
  const ds = agents.map((a) => a.durationMs).filter((d): d is number => d != null);
  return ds.length ? Math.max(...ds) : null;
}
