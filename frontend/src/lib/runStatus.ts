import type { WorkflowRunStatus } from "@/api/workflows";

/**
 * The one friendly word for a run / turn status, shared by the Runs list and the Session Room so both surfaces speak
 * the same lexicon — the raw enum ("Success" / "Enqueued") never reaches a user. "Working" is the active form of a
 * running run; a caller that needs the live-vs-terminal nuance (the Room's turn pill treats any in-flight turn as
 * "Working") resolves liveness itself and only falls back here for the terminal words. Pure + total.
 */
export function statusWord(status: WorkflowRunStatus): string {
  switch (status) {
    case "Success": return "Done";
    case "Failure": return "Failed";
    case "Cancelled": return "Stopped";
    case "Suspended": return "Waiting";
    case "Running": return "Working";
    case "Pending":
    case "Enqueued": return "Queued";
    default: return status;   // forward-compatible: an unknown future status shows verbatim rather than blank
  }
}

/**
 * A1: the run OUTCOME words — the honest account of how the work ended, keyed by the backend's
 * `SupervisorStopKind` + `AcceptanceFailed` vocabulary. Only a supervisor run carries one; `Succeeded` is
 * deliberately absent because a clean run has nothing to add to "Done".
 *
 * Note what these words are NOT: "Stopped" is already spent on a user-cancelled run, and conflating "I stopped
 * this" with "it gave up" would replace one misleading word with another. Each kind gets its own account.
 */
const OUTCOME_WORDS: Record<string, string> = {
  GaveUp: "Gave up",
  Forced: "Cut short",
  NeedsClarification: "Needs input",
  AcceptanceFailed: "Checks failed",
};

/** True when the run finished but did not cleanly achieve its goal — the case a bare "Done" misrepresents. */
export function isDegradedOutcome(outcome?: string | null): boolean {
  return !!outcome && outcome in OUTCOME_WORDS;
}

/**
 * The friendly word for a run, preferring its honest OUTCOME over the graph-level status when the two disagree.
 * A degraded supervisor run finishes as `Success` — the graph did complete — so the status word alone would call
 * a give-up "Done".
 *
 * Only a `Success` is ever overridden: every other status is ALREADY the honest word, and a failed run that also
 * gave up must keep reading "Failed" — replacing it with "Gave up" would hide the failure behind the softer
 * account. Falls back to {@link statusWord} for a clean run, a non-supervisor run, an unknown future outcome
 * value, and every run that predates the column: absence is not a verdict.
 */
export function outcomeWord(status: WorkflowRunStatus, outcome?: string | null): string {
  return status === "Success" && isDegradedOutcome(outcome) ? OUTCOME_WORDS[outcome!] : statusWord(status);
}
