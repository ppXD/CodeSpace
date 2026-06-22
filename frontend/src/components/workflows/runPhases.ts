import type { RunPhase, WorkflowRunStatus } from "@/api/workflows";

/**
 * An agent ref's open status counts as "in flight" while it is still working. Whitelisting the busy
 * states (rather than blacklisting terminal ones) degrades safe: an unknown status reads as not-busy.
 * Keep this in step with the non-terminal `AgentRunStatus` names — a new in-flight state (e.g. a
 * provisioning step) must be added here or the active tally undercounts.
 */
export function isAgentBusy(status: string): boolean {
  return status === "Running" || status === "Queued";
}

/**
 * A glanceable summary of WHERE a run is — the data behind the header's "control state" sentence
 * (e.g. "Running · Implement phase · 2 of 4 agents active · 1 waiting"). Derived purely from the
 * run status + the phase tree, so it works for any run shape (single agent, workflow, supervisor).
 */
export interface RunStateSummary {
  /** The run's status word (Running / Success / …) — the lead. */
  lead: WorkflowRunStatus;
  /** The current focus: the active phase's label, falling back to a waiting phase, "" when neither. */
  focus: string;
  /** Agents in flight, of the total the run fanned out to. */
  activeAgents: number;
  totalAgents: number;
  /** Phases parked on a human signal (approval / unanswered ask_human) — what needs you. */
  waiting: number;
}

export function summarizeRunState(runStatus: WorkflowRunStatus, phases: readonly RunPhase[]): RunStateSummary {
  const active = phases.find((p) => p.status === "Active");
  const waitingPhase = phases.find((p) => p.status === "Waiting");

  // The run-level tally must count each agent ONCE — a phased supervisor run lists the same agentRunId
  // in both its spawn-decision phase and its model-authored semantic phase, so summing per-phase metrics
  // would double-count. (The per-phase roll-up in RunOutline stays per-phase and is correct as-is.)
  const agentsById = new Map(phases.flatMap((p) => p.agents).map((a) => [a.agentRunId, a]));

  return {
    lead: runStatus,
    focus: active?.label ?? waitingPhase?.label ?? "",
    activeAgents: [...agentsById.values()].filter((a) => isAgentBusy(a.status)).length,
    totalAgents: agentsById.size,
    waiting: phases.filter((p) => p.status === "Waiting").length,
  };
}
