import { useState } from "react";

import { ApiError } from "@/api/request";
import { useConfirm } from "@/components/dialog/dialog-context";
import { useDeleteWorkflow, useRunWorkflowManually, useSetWorkflowEnabled, useWorkflow, useWorkflowRuns } from "@/hooks/use-workflows";

import { AgentOverviewPanel } from "./AgentOverviewPanel";
import { RunViewerDialog } from "./RunViewerDialog";
import { RunWorkflowModal } from "./RunWorkflowModal";

/**
 * Tab-content wrapper for the Agent detail shell — the thin orchestration around the pure
 * AgentOverviewPanel: data-loading, the manual-run flow, the lifecycle controls (enable/pause acts
 * immediately; delete confirms), and the run viewer. Kept in its own file (exports only components) so the route stays
 * lean and React Fast Refresh works here. The pure panel carries the rendering + its tests; this wrapper
 * carries the wiring.
 */

/** Overview tab — agent summary + lifecycle controls + the full run list + the manual-run flow. */
export function OverviewTab({ workflowId, onEditSource, onDeleted }: { workflowId: string; onEditSource: () => void; onDeleted: () => void }) {
  const workflow = useWorkflow(workflowId);
  const runs = useWorkflowRuns(workflowId);
  const runManually = useRunWorkflowManually();
  const setEnabled = useSetWorkflowEnabled();
  const remove = useDeleteWorkflow();
  const confirm = useConfirm();
  const [runFormOpen, setRunFormOpen] = useState(false);
  const [viewerRunId, setViewerRunId] = useState<string | null>(null);

  if (workflow.isLoading) return <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>;

  if (workflow.error instanceof ApiError || !workflow.data) {
    return (
      <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
        <div className="cn-banner-h">Agent not found</div>
        <div className="cn-banner-p">{workflow.error?.message ?? "It may have been deleted."}</div>
      </div>
    );
  }

  const wf = workflow.data;
  const inputs = wf.definition.inputs ?? [];

  const startRun = async (payload?: Record<string, unknown>) => {
    const result = await runManually.mutateAsync({ workflowId, payload });
    setRunFormOpen(false);
    setViewerRunId(result.runId);
  };

  const onRun = () => {
    // Declared inputs → collect them first (same form as the canvas Run); else run immediately.
    if (inputs.length > 0) { runManually.reset(); setRunFormOpen(true); return; }
    void startRun().catch(() => {});
  };

  // Toggling enable/pause acts immediately (no confirm) — it's cheap and reversible. Delete still confirms.
  const onToggleEnabled = () => setEnabled.mutate({ workflowId, enabled: !wf.enabled });

  const onDelete = async () => {
    const ok = await confirm({
      title: "Delete workflow?",
      message: <><strong>{wf.name}</strong> will be removed. Runs already in flight will finish on their own; new triggers stop firing immediately.</>,
      confirmLabel: "Delete",
      destructive: true,
    });
    if (!ok) return;
    remove.mutate(workflowId, { onSuccess: onDeleted });
  };

  return (
    <>
      <AgentOverviewPanel
        workflow={wf}
        runs={runs.data ?? []}
        onRun={onRun}
        onEditSource={onEditSource}
        onOpenRun={setViewerRunId}
        onToggleEnabled={onToggleEnabled}
        onDelete={onDelete}
        running={runManually.isPending}
        toggling={setEnabled.isPending}
        deleting={remove.isPending}
      />
      {runFormOpen && (
        <RunWorkflowModal
          workflowName={wf.name}
          inputs={inputs}
          pending={runManually.isPending}
          error={runManually.error instanceof ApiError ? runManually.error.message : null}
          onRun={(payload) => { void startRun(payload).catch(() => {}); }}
          onClose={() => setRunFormOpen(false)}
        />
      )}
      {viewerRunId && <RunViewerDialog runId={viewerRunId} onClose={() => setViewerRunId(null)} />}
    </>
  );
}
