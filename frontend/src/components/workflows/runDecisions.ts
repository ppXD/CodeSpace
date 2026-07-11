import type { DecisionType, PendingDecision } from "@/api/workflows";

/**
 * The run's own pending decisions — the team-wide queue narrowed to THIS run. Two match paths cover the two grains:
 *   • NODE grain (a flow.decision raised directly in this workflow run): `rootTraceId === runId`.
 *   • AGENT grain (an agent.run mid-run decision.request): its `rootTraceId`/`agentRunId` is the AGENT run's id,
 *     never this workflow run's, so we also match `agentRunId` against the agent runs this run fanned out to
 *     (`runAgentIds`, from the phase projection).
 * Known gap: a decision parked inside a nested flow.subworkflow CHILD run carries the child's id on both keys and
 * won't surface on the parent until the backend denormalizes the true tree-root id. A per-run endpoint is future work.
 */
export function decisionsForRun(decisions: readonly PendingDecision[], runId: string, runAgentIds?: ReadonlySet<string>): PendingDecision[] {
  return decisions.filter((d) =>
    d.rootTraceId === runId || (d.agentRunId != null && runAgentIds != null && runAgentIds.has(d.agentRunId)));
}

/**
 * True for the decision shapes answered by a SINGLE option click (one tap decides) — confirm, choose-one, and the
 * approve/reject gate. choose_many and free_text instead compose a draft and submit. An unknown type degrades to a
 * free-text answer (safe: the queue still lets a human respond), so it is NOT single-choice.
 */
export function isSingleChoice(decisionType: DecisionType | string): boolean {
  return decisionType === "confirm" || decisionType === "choose_one" || decisionType === "approve_action";
}

/** A compact, human countdown to a decision's bounded deadline; null when there is no deadline. */
export function deadlineLabel(deadlineAt: string | null | undefined, nowMs: number): string | null {
  if (!deadlineAt) return null;

  const ms = new Date(deadlineAt).getTime() - nowMs;
  if (ms <= 0) return "due now";
  if (ms < 60000) return "<1m left";   // don't round a sub-minute deadline down to a misleading "0m left"

  const mins = Math.round(ms / 60000);
  if (mins < 60) return `${mins}m left`;

  const hrs = Math.round(mins / 60);
  if (hrs < 24) return `${hrs}h left`;

  return `${Math.round(hrs / 24)}d left`;
}
