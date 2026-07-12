import type { WorkflowVariable } from "@/api/workflows";

import type { ScopeSuggestion } from "./scope-introspection";
import { VariablePickerInput } from "./VariablePickerInput";

export interface TerminalEditorProps {
  /** The workflow's DECLARED outputs (name + type) — authored in the Outputs side panel. */
  outputs: WorkflowVariable[];
  /** The End node's inputs — the value map the engine resolves into the run's WorkflowOutputs. */
  inputs: Record<string, unknown>;
  onInputsChange: (next: Record<string, unknown>) => void;
  suggestions: ScopeSuggestion[];
}

/**
 * The End node's Inputs ARE the workflow's output VALUES: on reaching a successful terminal the engine resolves
 * this bag against the run scope and it becomes the run's outputs (WorkflowEngine → WorkflowOutputs). The node
 * carries no behaviour, so its schemas are empty — meaning the generic form shows "No configuration" and the
 * output map was previously UNAUTHORABLE. This editor drives that map directly: one row per DECLARED workflow
 * output, each bound to a literal value or an upstream {{ref}}. Declaring the output NAMES stays in the Outputs
 * side panel (`definition.outputs`); this binds each name to what the run actually returns.
 */
export function TerminalEditor({ outputs, inputs, onInputsChange, suggestions }: TerminalEditorProps) {
  if (outputs.length === 0) {
    return (
      <section className="wf-inspector-section">
        <p className="wf-retry-hint">
          This workflow declares no outputs yet. Add them in the <b>Outputs</b> side panel, then map each to a
          value here — the End node returns them when the run succeeds.
        </p>
      </section>
    );
  }

  const setBinding = (name: string, next: string) =>
    onInputsChange({ ...inputs, [name]: next === "" ? undefined : next });

  const currentValue = (name: string): string => {
    const v = inputs[name];
    return typeof v === "string" ? v : v != null ? String(v) : "";
  };

  return (
    <section className="wf-inspector-section">
      <div className="wf-inspector-section-h">Workflow outputs</div>
      <p className="wf-form-help">
        Each declared output → the value this run returns. Type <code>@</code> to bind an earlier step&apos;s output.
      </p>
      {outputs.map((o) => (
        <div key={o.name} className="wf-form-row">
          <span className="wf-form-label">{o.label ?? o.name}</span>
          <VariablePickerInput
            value={currentValue(o.name)}
            onChange={(next) => setBinding(o.name, next)}
            suggestions={suggestions}
            placeholder={`Bind ${o.name} — type @ for an earlier output`}
          />
          {o.description && <span className="wf-form-help">{o.description}</span>}
        </div>
      ))}
    </section>
  );
}
