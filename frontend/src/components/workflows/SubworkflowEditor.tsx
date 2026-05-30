import { useMemo } from "react";

import type { WorkflowVariable } from "@/api/workflows";
import { useWorkflow, useWorkflows } from "@/hooks/use-workflows";

import type { ScopeSuggestion } from "./scope-introspection";
import { SchemaForm } from "./SchemaForm";

/**
 * Inspector for a <c>flow.subworkflow</c> node: pick the child workflow, then map the parent's
 * values onto the CHILD's declared inputs. The mapping reuses {@link SchemaForm} — built from the
 * selected child's input declarations — so every per-type control + the `{{}}` variable picker
 * work exactly as on any other node. Replaces the raw "paste a GUID" text field.
 *
 * On-disk shape (the engine reads these): config = `{ workflowId, version? }`, inputs =
 * `{ inputs: { &lt;childInputName&gt;: value } }`.
 */
interface SubworkflowEditorProps {
  config: Record<string, unknown>;
  inputs: Record<string, unknown>;
  onConfigChange: (next: Record<string, unknown>) => void;
  onInputsChange: (next: Record<string, unknown>) => void;
  /** Parent-scope variables for the input-mapping picker. */
  suggestions: ScopeSuggestion[];
  /** The workflow being edited — excluded from the picker to avoid the obvious self-recursion footgun. */
  currentWorkflowId: string;
}

export function SubworkflowEditor({ config, inputs, onConfigChange, onInputsChange, suggestions, currentWorkflowId }: SubworkflowEditorProps) {
  const workflowId = typeof config.workflowId === "string" ? config.workflowId : "";

  const list = useWorkflows();
  const child = useWorkflow(workflowId || null);

  // Other workflows in the team. Excluding self only blocks the obvious direct loop; the backend
  // still depth-guards deeper indirect recursion (A → B → A).
  const options = useMemo(
    () => (list.data ?? []).filter((w) => w.id !== currentWorkflowId),
    [list.data, currentWorkflowId],
  );

  const childInputs = useMemo(
    () => child.data?.definition.inputs ?? [],
    [child.data],
  );

  // A JSON schema synthesised from the child's declared inputs, so SchemaForm renders one
  // picker-aware control per input.
  const inputsSchema = useMemo(() => ({
    type: "object",
    properties: Object.fromEntries(childInputs.map((v) => [v.name, asFieldSchema(v)])),
    required: childInputs.filter((v) => v.required).map((v) => v.name),
  }), [childInputs]);

  const mappedInputs = (typeof inputs.inputs === "object" && inputs.inputs !== null ? inputs.inputs : {}) as Record<string, unknown>;

  const pickWorkflow = (id: string) => {
    onConfigChange({ ...config, workflowId: id || undefined });
    // The old mapping keys belonged to a different child — clear them when the target changes.
    onInputsChange({ inputs: {} });
  };

  return (
    <>
      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Sub-workflow</div>
        <label className="wf-form-row">
          <span className="wf-form-label">Workflow</span>
          <select
            className="wf-form-input"
            value={workflowId}
            onChange={(e) => pickWorkflow(e.target.value)}
            disabled={list.isLoading}
          >
            <option value="">{list.isLoading ? "Loading…" : "Select a workflow…"}</option>
            {options.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select>
        </label>
        <p className="wf-retry-hint">
          Runs the chosen workflow as a step; its outputs become this node's outputs. If it fails,
          the node takes its error branch (or fails the run).
        </p>
      </section>

      {workflowId && (
        <section className="wf-inspector-section">
          <div className="wf-inspector-section-h">Inputs to the sub-workflow</div>
          {child.isLoading ? (
            <div className="wf-inputs-empty">Loading the workflow's inputs…</div>
          ) : childInputs.length === 0 ? (
            <div className="wf-inputs-empty">This workflow declares no inputs.</div>
          ) : (
            <SchemaForm
              schema={inputsSchema}
              value={mappedInputs}
              onChange={(v) => onInputsChange({ inputs: v })}
              templateHint
              variableSuggestions={suggestions}
            />
          )}
        </section>
      )}
    </>
  );
}

/** The child input's own JSON-schema fragment drives its control; carry label/description through. */
function asFieldSchema(v: WorkflowVariable): Record<string, unknown> {
  const base: Record<string, unknown> = (typeof v.schema === "object" && v.schema !== null) ? { ...(v.schema as Record<string, unknown>) } : { type: "string" };
  if (v.label) base.title = v.label;
  if (v.description) base.description = v.description;
  return base;
}
