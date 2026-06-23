import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunStatus } from "@/api/workflows";
import { isRunActive, useCancelRun } from "@/hooks/use-workflows";

/**
 * The run's hard-stop control — cancels a still-live run (kills its in-flight agents) over the existing
 * POST /cancel endpoint. Renders NOTHING for a terminal run. Because a stop can't be undone (you Re-run a fresh
 * copy), it's a deliberate two-step: a neutral "Stop" arms a red "Confirm stop" + "Keep running". On success the
 * run goes terminal and this control disappears on the next status poll/invalidation.
 */
export function StopRunButton({ runId, status }: { runId: string; status: WorkflowRunStatus }) {
  const cancel = useCancelRun(runId);
  const [confirming, setConfirming] = useState(false);

  if (!isRunActive(status)) return null;   // a finished run has nothing to stop

  if (!confirming) {
    return (
      <button
        className="btn"
        onClick={() => setConfirming(true)}
        title="Cancel this run and kill any in-flight agents. This can't be undone — you can Re-run a fresh copy."
      >
        <Ic.CircleStop size={13} /> Stop
      </button>
    );
  }

  return (
    <>
      <button className="btn btn-danger" onClick={() => cancel.mutate()} disabled={cancel.isPending}>
        <Ic.CircleStop size={13} /> {cancel.isPending ? "Stopping…" : "Confirm stop"}
      </button>
      <button className="btn" onClick={() => setConfirming(false)} disabled={cancel.isPending}>
        Keep running
      </button>
      {cancel.isError && <span className="ct-action-error">Couldn’t stop — try again.</span>}
    </>
  );
}
