import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowRunStatus } from "@/api/workflows";
import { useConfirm } from "@/components/dialog";
import { isRunActive, useCancelRun } from "@/hooks/use-workflows";

/**
 * The run's hard-stop control — a single "Stop" button that opens a confirm modal (the run can't be un-cancelled,
 * and it kills any in-flight agents, so it's a deliberate confirm) and, on OK, cancels the run over the existing
 * POST /cancel endpoint. Renders NOTHING for a terminal run; on success the run goes terminal and this control
 * disappears on the next status poll.
 */
export function StopRunButton({ runId, status }: { runId: string; status: WorkflowRunStatus }) {
  const cancel = useCancelRun(runId);
  const confirm = useConfirm();

  if (!isRunActive(status)) return null;   // a finished run has nothing to stop

  const onStop = async () => {
    const ok = await confirm({
      title: "Stop this run?",
      message: "This cancels the run and kills any in-flight agents. It can’t be undone — you can Re-run a fresh copy.",
      confirmLabel: "Stop run",
      cancelLabel: "Keep running",
      destructive: true,
    });

    if (ok) cancel.mutate();
  };

  return (
    <button className="btn" onClick={() => void onStop()} disabled={cancel.isPending} title="Cancel this run and kill any in-flight agents. This can't be undone.">
      <Ic.CircleStop size={13} /> {cancel.isPending ? "Stopping…" : "Stop"}
    </button>
  );
}
