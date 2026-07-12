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

/** Elapsed time between two ISO timestamps as "45s" / "7m 59s" / "1h 5m"; empty when either is missing. */
export function formatDuration(startISO: string | null, endISO: string | null): string {
  if (!startISO || !endISO) return "";

  const sec = Math.max(0, Math.round((new Date(endISO).getTime() - new Date(startISO).getTime()) / 1000));
  if (!Number.isFinite(sec)) return "";   // an unparseable timestamp → no duration rather than "NaNm NaNs"
  if (sec < 60) return `${sec}s`;

  const m = Math.floor(sec / 60);
  if (m < 60) return `${m}m ${sec % 60}s`;

  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}

/** The status tone shared by the row's tinted status tile + its status word. */
export type RunStatusTone = "ok" | "err" | "running" | "suspended" | "cancelled" | "queued";

export function runStatusTone(status: WorkflowRunStatus): RunStatusTone {
  if (status === "Success") return "ok";
  if (status === "Failure") return "err";
  if (status === "Running") return "running";
  if (status === "Suspended") return "suspended";
  if (status === "Cancelled") return "cancelled";

  return "queued";   // Pending / Enqueued
}

/** The 2-3 highest-frequency synthesized node ids → a plain-English name. Kept intentionally small; any other id falls
 *  back to a de-quoted form, so a new engine node never produces a broken label. */
const NODE_LABELS: Record<string, string> = { sup: "coordinator", map: "fan-out", syn: "synthesizer" };

/**
 * Humanize a raw engine run-error for display. The engine reports a step failure as `Node '<id>' failed.`
 * (optionally `… in map '<id>' failed.`) using the node's internal id — meaningless to a user. Rewrite it to
 * "The coordinator step failed." (mapping the common ids, else a de-quoted fallback). Any error that isn't this
 * shape is returned VERBATIM, so a real message is never mangled. Pure + total.
 */
export function humanizeRunError(error: string): string {
  const m = /^Node '([^']+)'(?: in map '[^']+')? failed\.$/.exec(error.trim());
  if (!m) return error;

  return `The ${NODE_LABELS[m[1]] ?? m[1]} step failed.`;
}

/**
 * The run's wall-clock time for the row's clock chip. A terminal run shows its total duration (createdDate→completedAt,
 * NOT startedAt→completedAt — startedAt is reset on every suspend→resume re-dispatch, so it collapses to ~0s for any
 * run that parked); a live run shows elapsed-so-far; a parked run its wait age. Empty when there's nothing meaningful
 * yet — a freshly queued run, or a terminal run still missing its completedAt.
 */
export function runDuration(run: WorkflowRunSummary, nowMs: number): string {
  if (run.status === "Suspended") return `waiting ${compactAge(run.createdDate, nowMs)}`;
  if (run.status === "Running") return compactAge(run.startedAt ?? run.createdDate, nowMs);
  if (run.status === "Pending" || run.status === "Enqueued") return "";

  // A terminal run that ever parked: createdDate→completedAt is a lifespan dominated by wait time, not runtime — show
  // it as a coarse "open Nd" span (honest) rather than the bogus multi-hour clock a parked-then-cancelled run yields.
  if (run.wasSuspended && run.completedAt) return `open ${compactAge(run.createdDate, new Date(run.completedAt).getTime())}`;

  return formatDuration(run.createdDate, run.completedAt);   // Success / Failure / Cancelled that ran straight through
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
export type CockpitFilter = "attention" | "live" | "failed" | "today" | null;

/** The entity/scope dimensions the run filter bar controls — the single source of truth shared by the bar and the
 *  cockpit's URL search contract, so a filtered view is fully deep-linkable. Every dimension is a string-id array. */
export const BAR_DIMS = ["runKinds", "repositoryIds", "projectIds", "actorIds", "agentDefinitionIds"] as const;
