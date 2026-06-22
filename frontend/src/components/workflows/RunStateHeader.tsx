import type { RunPhase, WorkflowRunStatus } from "@/api/workflows";

import { summarizeRunState } from "./runPhases";

/**
 * The run's "control state" sentence — one glance at WHERE the run is and WHAT needs you, composed purely from
 * the run status + the phase tree (e.g. "Running · Implement · 2 of 4 agents active · 1 waiting"). The waiting
 * count is the slice-1 decision signal (phases parked on an approval / unanswered ask_human); the rich decision
 * inbox lands in a later slice. Renders nothing extra for a clean terminal run beyond its status + agent tally.
 */
export function RunStateHeader({ runStatus, phases }: { runStatus: WorkflowRunStatus; phases: readonly RunPhase[] }) {
  const s = summarizeRunState(runStatus, phases);

  // One clean sentence for assistive tech — the visual `·`-separated spans are aria-hidden noise on
  // their own. role="status" announces transitions politely as the 2s poll advances the run.
  const label = [
    s.lead,
    s.focus,
    s.totalAgents > 0 ? `${s.activeAgents} of ${s.totalAgents} agents active` : null,
    s.waiting > 0 ? `${s.waiting} waiting` : null,
  ].filter(Boolean).join(", ");

  return (
    <div className="run-state" data-status={runStatus.toLowerCase()} role="status" aria-label={label}>
      <span className="run-state-lead" aria-hidden="true">{s.lead}</span>

      {s.focus && (
        <><span className="run-state-sep" aria-hidden="true">·</span><span className="run-state-focus" aria-hidden="true">{s.focus}</span></>
      )}

      {s.totalAgents > 0 && (
        <><span className="run-state-sep" aria-hidden="true">·</span><span aria-hidden="true">{s.activeAgents} of {s.totalAgents} agents active</span></>
      )}

      {s.waiting > 0 && (
        <><span className="run-state-sep" aria-hidden="true">·</span><span className="run-state-waiting" aria-hidden="true">{s.waiting} waiting</span></>
      )}
    </div>
  );
}
