import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunStatus } from "@/api/workflows";
import { useContinueRun } from "@/hooks/use-workflows";

/**
 * The run's "Continue" control — for a STRANDED Suspended run (Suspended with NO outstanding wait): one that parked
 * and isn't getting re-dispatched. Clicking drives the SAME re-dispatch the reconciler performs (CAS Suspended→Pending
 * + dispatch), on demand, instead of waiting for the ≤2-min sweep. Renders NOTHING unless the run is Suspended AND has
 * no pending wait — a run parked on an approval / timer / callback resumes via its wait (the SuspendedPanel), not here.
 * The backend re-checks both conditions, so a lost race is a clean no-op (the status poll then hides this control).
 */
export function ContinueRunButton({ runId, status, hasPendingWait }: { runId: string; status: WorkflowRunStatus; hasPendingWait: boolean }) {
  const cont = useContinueRun(runId);

  if (status !== "Suspended" || hasPendingWait) return null;

  return (
    <button className="btn" onClick={() => cont.mutate()} disabled={cont.isPending} title="Continue this stranded run — it paused with no outstanding wait. Drives the same recovery the system performs on its own.">
      <Ic.Play size={13} /> {cont.isPending ? "Continuing…" : "Continue"}
    </button>
  );
}
