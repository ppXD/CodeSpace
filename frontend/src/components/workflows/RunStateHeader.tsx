import type { RunPhase, WorkflowRunStatus } from "@/api/workflows";

import { isRunActive } from "@/hooks/use-workflows";

import { summarizeRunState } from "./runPhases";

/**
 * The run's "control state" sentence — one glance at WHERE the run is and WHAT needs you, composed purely from
 * the run status + the phase tree (e.g. "Running · Implement · 2 of 4 agents active · 1 decision needs you").
 * `pendingDecisions` is the real "needs you" signal from the decision inbox — when present it supersedes the
 * coarser phase-Waiting count; without it the header falls back to that count. Renders nothing extra for a clean
 * terminal run beyond its status + agent tally.
 */
export function RunStateHeader({ runStatus, phases, pendingDecisions }: { runStatus: WorkflowRunStatus; phases: readonly RunPhase[]; pendingDecisions?: number }) {
  const s = summarizeRunState(runStatus, phases);

  // The agent tally is a LIVE-progress phrase while the run runs ("2 of 4 agents active"); once the run is
  // terminal "0 of 1 active" reads as nonsense, so it settles to a plain count ("Success · 1 agent").
  const agentClause = s.totalAgents === 0 ? null
    : isRunActive(runStatus) ? `${s.activeAgents} of ${s.totalAgents} agents active`
    : `${s.totalAgents} ${s.totalAgents === 1 ? "agent" : "agents"}`;

  // The sharp signal (answerable decisions) wins over the proxy (phases parked) when the inbox has loaded.
  const needsYou = pendingDecisions != null && pendingDecisions > 0
    ? `${pendingDecisions} ${pendingDecisions === 1 ? "decision needs" : "decisions need"} you`
    : pendingDecisions == null && s.waiting > 0 ? `${s.waiting} waiting` : null;

  // One clean sentence for assistive tech — the visual `·`-separated spans are aria-hidden noise on
  // their own. role="status" announces transitions politely as the 2s poll advances the run.
  const label = [s.lead, s.focus, agentClause, needsYou].filter(Boolean).join(", ");

  return (
    <div className="run-state" data-status={runStatus.toLowerCase()} role="status" aria-label={label}>
      <span className="run-state-lead" aria-hidden="true">{s.lead}</span>

      {s.focus && (
        <><span className="run-state-sep" aria-hidden="true">·</span><span className="run-state-focus" aria-hidden="true">{s.focus}</span></>
      )}

      {agentClause && (
        <><span className="run-state-sep" aria-hidden="true">·</span><span aria-hidden="true">{agentClause}</span></>
      )}

      {needsYou && (
        <><span className="run-state-sep" aria-hidden="true">·</span><span className="run-state-waiting" aria-hidden="true">{needsYou}</span></>
      )}
    </div>
  );
}
