/** Status pill shared across the run-detail view, run lists, and agent cards. */
export function RunStatusBadge({ status, title }: { status: string; title?: string }) {
  // Enqueued = workflow-run "claimed by dispatcher, waiting for worker pickup"; Queued = an agent run not yet
  // claimed by its worker — both read as a pending-queue tone.
  const tone =
    status === "Success" || status === "Succeeded" ? "ok"
    : status === "Failure" || status === "Failed" || status === "TimedOut" ? "err"
    : status === "Cancelled" || status === "Skipped" ? "muted"
    : status === "Enqueued" || status === "Queued" ? "queued"
    : status === "Suspended" ? "suspended"
    : "running";

  return <span className={`wf-status-pill wf-status-${tone}`} title={title}>{status}</span>;
}
