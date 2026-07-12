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
