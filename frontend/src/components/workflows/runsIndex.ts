import type { WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

/**
 * The runs index split into the three zones the page reads top-to-bottom: what needs a human, what's in flight,
 * and what's done. Order within each zone is preserved from the (newest-first) input. The split is by run status
 * only — a Suspended run is parked on a human signal (approval / ask_human / decision), so it's the attention zone.
 */
export interface RunBuckets {
  needsAttention: WorkflowRunSummary[];
  live: WorkflowRunSummary[];
  recent: WorkflowRunSummary[];
}

const LIVE_STATUSES = new Set<WorkflowRunStatus>(["Pending", "Enqueued", "Running"]);

export function bucketRuns(runs: readonly WorkflowRunSummary[]): RunBuckets {
  const needsAttention: WorkflowRunSummary[] = [];
  const live: WorkflowRunSummary[] = [];
  const recent: WorkflowRunSummary[] = [];

  for (const r of runs) {
    if (r.status === "Suspended") needsAttention.push(r);
    else if (LIVE_STATUSES.has(r.status)) live.push(r);
    else recent.push(r);
  }

  return { needsAttention, live, recent };
}

/** A friendly source-type label — title-cased from the open `source_type` token (manual / webhook / schedule.cron / …). */
export function sourceLabel(sourceType: string): string {
  if (!sourceType) return "Run";
  return sourceType.charAt(0).toUpperCase() + sourceType.slice(1);
}
