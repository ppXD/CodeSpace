import { useState } from "react";

import { ApiError } from "@/api/request";
import { useConfirm } from "@/components/dialog/dialog-context";
import { useDeleteWorkflow, useRunWorkflowManually, useSetWorkflowEnabled, useWorkflow, useWorkflowRuns } from "@/hooks/use-workflows";

import { AgentActivityPanel } from "./AgentActivityPanel";
import { AgentOverviewPanel } from "./AgentOverviewPanel";
import { AgentSettingsPanel } from "./AgentSettingsPanel";
import { RunViewerDialog } from "./RunViewerDialog";
import { RunWorkflowModal } from "./RunWorkflowModal";

/**
 * Tab-content wrappers for the Agent detail shell — the thin orchestration around the pure
 * AgentOverviewPanel / AgentActivityPanel: data-loading, the manual-run flow, and the run viewer.
 * Kept in their own file (exports only components) so the route stays lean and React Fast Refresh
 * works here. The pure panels carry the rendering logic + its tests; these wrappers carry the wiring.
 */

/** Overview tab — read-only agent summary + the manual-run flow (reuses the run modal + viewer). */
export function OverviewTab({ workflowId, onEditSource, onViewActivity }: { workflowId: string; onEditSource: () => void; onViewActivity?: () => void }) {
  const workflow = useWorkflow(workflowId);
  const runs = useWorkflowRuns(workflowId);
  const runManually = useRunWorkflowManually();
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

  const inputs = workflow.data.definition.inputs ?? [];

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

  return (
    <>
      <AgentOverviewPanel workflow={workflow.data} recentRuns={runs.data ?? []} onRun={onRun} onEditSource={onEditSource} onViewActivity={onViewActivity} running={runManually.isPending} />
      {runFormOpen && (
        <RunWorkflowModal
          workflowName={workflow.data.name}
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

/** Activity tab — the agent's run list; opening a row shows the run viewer inline (stays on the page). */
export function ActivityTab({ workflowId }: { workflowId: string }) {
  const [viewerRunId, setViewerRunId] = useState<string | null>(null);
  return (
    <>
      <AgentActivityPanel workflowId={workflowId} onOpenRun={setViewerRunId} />
      {viewerRunId && <RunViewerDialog runId={viewerRunId} onClose={() => setViewerRunId(null)} />}
    </>
  );
}

/** Settings tab — lifecycle + governance for the agent. Enable/pause and delete each use their own
 * dedicated endpoint (no full-PUT, so the canvas definition is never touched); delete confirms via
 * the themed dialog, then `onDeleted` navigates away. */
export function SettingsTab({ workflowId, onDeleted }: { workflowId: string; onDeleted: () => void }) {
  const workflow = useWorkflow(workflowId);
  const setEnabled = useSetWorkflowEnabled();
  const remove = useDeleteWorkflow();
  const confirm = useConfirm();

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

  const onToggleEnabled = () => setEnabled.mutate({ workflowId, enabled: !wf.enabled });

  const onDelete = async () => {
    const ok = await confirm({
      title: "Delete agent?",
      message: <><strong>{wf.name}</strong> will be removed. Runs already in flight will finish on their own; new triggers stop firing immediately.</>,
      confirmLabel: "Delete",
      destructive: true,
    });
    if (!ok) return;
    remove.mutate(workflowId, { onSuccess: onDeleted });
  };

  return <AgentSettingsPanel workflow={wf} onToggleEnabled={onToggleEnabled} onDelete={onDelete} toggling={setEnabled.isPending} deleting={remove.isPending} />;
}
