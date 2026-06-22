import type { NodeStatus } from "@/api/workflows";

/** Tally a run's aggregated node statuses into the progress chip's counts; null when nothing has run. */
export function summarizeRunProgress(statuses: Map<string, NodeStatus>): { success: number; running: number; failure: number } | null {
  const c = { success: 0, running: 0, failure: 0 };
  for (const s of statuses.values()) {
    if (s === "Success") c.success++;
    else if (s === "Running" || s === "Suspended") c.running++;   // a node parked on an agent/approval still reads as "in flight"
    else if (s === "Failure") c.failure++;
  }
  return c.success + c.running + c.failure === 0 ? null : c;
}
