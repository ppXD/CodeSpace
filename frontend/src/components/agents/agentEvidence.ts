import type { AgentRunStatus } from "@/api/agents";

/**
 * Pure formatters + tone mappings for an agent roster row's run evidence — extracted so the sparkline / stat
 * pills are unit-testable without the DOM. Mirrors the AgentScorecardPanel's number formatting (whole-percent
 * success, compact duration, em-dash for absent) so the roster reads consistently with the fleet view.
 */

/** 0..1 → a whole-number percentage ("70%"). */
export function formatSuccessRate(rate: number): string {
  return `${Math.round(rate * 100)}%`;
}

/** Avatar initials from a persona name: the first letter of its first two words, or the first two letters of a
 *  single word ("Bug Report" → "BR", "backend" → "BE"). Upper-cased; falls back to "?" for a blank name. */
export function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return "?";
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return (words[0][0] + words[1][0]).toUpperCase();
}

/** Seconds → a compact human duration ("8s", "1m 30s", "2h 5m"); an em-dash when there's no latency to show. */
export function formatDuration(seconds: number | null): string {
  if (seconds === null) return "—";

  // Round to whole seconds FIRST, then bucket — otherwise a fractional remainder can round up to 60 and print
  // "1m 60s" (or "60s" in the sub-minute branch).
  const total = Math.round(seconds);
  if (total < 60) return `${total}s`;

  const mins = Math.floor(total / 60);
  if (mins < 60) {
    const rem = total % 60;
    return rem === 0 ? `${mins}m` : `${mins}m ${rem}s`;
  }

  const hours = Math.floor(mins / 60);
  const remMins = mins % 60;
  return remMins === 0 ? `${hours}h` : `${hours}h ${remMins}m`;
}

/** Estimated spend → "$1.94"; an em-dash when nothing could be priced (null, distinct from $0.00). */
export function formatCost(usd: number | null): string {
  if (usd === null) return "—";
  return `$${usd.toFixed(2)}`;
}

/**
 * The copy for a row that has no stat entry — it must NOT always claim "No runs yet", because an empty entry has four
 * distinct causes: the stats query is still loading, it errored, a bounded window simply excluded this agent's older
 * runs, or the agent genuinely never ran. Only the last is "No runs yet".
 */
export function emptyEvidenceLabel(opts: { pending: boolean; error: boolean; windowed: boolean }): string {
  if (opts.pending) return "Loading recent runs…";
  if (opts.error) return "Run stats unavailable";
  if (opts.windowed) return "No runs in this window";
  return "No runs yet — launch a task to see this agent's success rate and latency.";
}

const DANGER_OUTCOMES = new Set<AgentRunStatus>(["Failed", "TimedOut", "Cancelled", "NeedsReview"]);

/** A sparkline dot's tone: a success is good, any terminal non-success is danger, an in-flight run is a muted (live) dot. */
export function outcomeTone(status: AgentRunStatus): "good" | "danger" | "muted" {
  if (status === "Succeeded") return "good";
  if (DANGER_OUTCOMES.has(status)) return "danger";
  return "muted";
}

/**
 * The success-rate pill's tone. With no terminal runs yet the rate is meaningless → muted (the row shows "no scored
 * runs"), NOT a red 0%. Otherwise a simple band: healthy ≥70%, shaky ≥40%, poor below. Thresholds are a display
 * choice, kept here so the row and its test agree on the boundary.
 */
export function successTone(rate: number, total: number): "good" | "warn" | "danger" | "muted" {
  if (total === 0) return "muted";
  if (rate >= 0.7) return "good";
  if (rate >= 0.4) return "warn";
  return "danger";
}
