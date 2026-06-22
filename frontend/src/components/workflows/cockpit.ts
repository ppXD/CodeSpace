import type { PendingDecision, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

/**
 * The Runs cockpit metrics — pure derivations behind the four status cards. Everything here comes from data that
 * already exists (the team runs list + the cross-grain decision queue); cost/token usage is not tracked yet, so the
 * fourth card reports runs-today instead. All time-relative helpers take an explicit `nowMs` so they stay testable.
 */

/** A compact age like "14m" / "3h" / "2d" since an ISO timestamp; "now" under a minute. */
export function compactAge(iso: string, nowMs: number): string {
  const ms = nowMs - new Date(iso).getTime();
  if (ms < 60_000) return "now";

  const m = Math.floor(ms / 60_000);
  if (m < 60) return `${m}m`;

  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h`;

  return `${Math.floor(h / 24)}d`;
}

/** The Needs-decision card: how many decisions wait on a human, the oldest one's age, and how many are high-risk. */
export interface DecisionSummary {
  count: number;
  oldestAge: string | null;
  highRisk: number;
}

export function summarizeDecisions(decisions: readonly PendingDecision[], nowMs: number): DecisionSummary {
  if (decisions.length === 0) return { count: 0, oldestAge: null, highRisk: 0 };

  // Compare by parsed time, not the ISO string — robust if timestamps ever carry differing UTC offsets.
  const oldest = decisions.reduce((min, d) => (new Date(d.createdAt).getTime() < new Date(min).getTime() ? d.createdAt : min), decisions[0].createdAt);

  return {
    count: decisions.length,
    oldestAge: compactAge(oldest, nowMs),
    highRisk: decisions.filter((d) => d.riskLevel.toLowerCase() === "high").length,
  };
}

/** Run counts for the Live + Failed/stuck cards (a run is in exactly one bucket). */
export interface RunCounts {
  live: number;
  failed: number;
  suspended: number;
}

const LIVE_STATUSES = new Set<WorkflowRunStatus>(["Pending", "Enqueued", "Running"]);

export function countRuns(runs: readonly WorkflowRunSummary[]): RunCounts {
  let live = 0, failed = 0, suspended = 0;

  for (const r of runs) {
    if (LIVE_STATUSES.has(r.status)) live++;
    else if (r.status === "Failure") failed++;
    else if (r.status === "Suspended") suspended++;
  }

  return { live, failed, suspended };
}

/** Today's runs (by createdDate, local day) → the count + a 24-bucket hourly histogram for the card's sparkline. */
export interface TodaySummary {
  count: number;
  hourly: number[];
}

export function summarizeToday(runs: readonly WorkflowRunSummary[], nowMs: number): TodaySummary {
  const now = new Date(nowMs);
  const startOfDay = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const hourly = new Array<number>(24).fill(0);
  let count = 0;

  for (const r of runs) {
    const t = new Date(r.createdDate).getTime();
    if (t < startOfDay) continue;

    count++;
    hourly[Math.min(23, Math.floor((t - startOfDay) / 3_600_000))]++;
  }

  return { count, hourly };
}

/** The cockpit filter — which card is "armed", narrowing the zones below. `null` shows the full board. */
export type CockpitFilter = "decisions" | "live" | "failed" | "today" | null;
