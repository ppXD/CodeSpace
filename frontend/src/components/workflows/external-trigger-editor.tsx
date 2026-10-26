import { useWorkflowExternalTriggers } from "@/hooks/use-workflows";

import { ExternalTriggerInspector } from "./external-trigger-inspector";

interface IExternalTriggerEditorProps {
  workflowId: string;
  nodeId: string;
  config: Record<string, unknown>;
  onConfigChange: (next: Record<string, unknown>) => void;
}

export function ExternalTriggerEditor({ workflowId, nodeId, config, onConfigChange }: IExternalTriggerEditorProps) {
  const triggers = useWorkflowExternalTriggers(workflowId);

  if (triggers.isPending) return <section className="wf-inspector-section" role="status">Loading endpoint...</section>;
  if (triggers.isError) return (
    <section className="wf-inspector-section">
      <p role="alert">{triggers.error.message}</p>
      <button type="button" className="btn" onClick={() => void triggers.refetch()}>Retry</button>
    </section>
  );

  const currentTriggerId = triggers.data.triggers.find((trigger) => trigger.nodeId === nodeId)?.id ?? null;
  return <ExternalTriggerInspector key={`${workflowId}:${nodeId}:${currentTriggerId}`} config={config} currentTriggerId={currentTriggerId} onConfigChange={onConfigChange} />;
}
