import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowVariable } from "@/api/workflows";
import { schemaToFieldType } from "@/lib/inputFieldSchema";

import { AddInputFieldModal } from "./AddInputFieldModal";

interface StartNodeInputsEditorProps {
  inputs: WorkflowVariable[];
  onChange: (next: WorkflowVariable[]) => void;
}

/**
 * "Input fields" editor for the Manual start node's inspector — the Dify-style place to define
 * the per-run inputs directly on the entry node (rather than a separate side panel). Edits the
 * workflow's `definition.inputs`; each field opens the {@link AddInputFieldModal}. Pure
 * presentational shell over a `WorkflowVariable[]` value + onChange.
 */
export function StartNodeInputsEditor({ inputs, onChange }: StartNodeInputsEditorProps) {
  // null = modal closed; { index: null } = adding; { index: n } = editing row n.
  const [editing, setEditing] = useState<{ index: number | null } | null>(null);

  const upsert = (field: WorkflowVariable) => {
    onChange(editing?.index == null ? [...inputs, field] : inputs.map((v, i) => (i === editing.index ? field : v)));
    setEditing(null);
  };

  const remove = (index: number) => onChange(inputs.filter((_, i) => i !== index));

  const takenNames = (excludeIndex: number | null) => inputs.filter((_, i) => i !== excludeIndex).map((v) => v.name);

  return (
    <section className="wf-inspector-section">
      <div className="wf-inputs-head">
        <span className="wf-inspector-section-h">Input fields</span>
        <button type="button" className="btn btn-ghost wf-inputs-add" onClick={() => setEditing({ index: null })} title="Add field">
          <Ic.Plus size={13} />
        </button>
      </div>

      {inputs.length === 0 ? (
        <div className="wf-inputs-empty">No input fields yet. Add one to collect values when the workflow is run.</div>
      ) : (
        <ul className="wf-inputs-list">
          {inputs.map((v, i) => (
            <li key={i} className="wf-inputs-row" onClick={() => setEditing({ index: i })} title="Edit field">
              <Ic.Key size={12} />
              <span className="wf-inputs-row-name">{v.name}</span>
              {v.label && <span className="wf-inputs-row-label">· {v.label}</span>}
              <span className="wf-inputs-row-type">{schemaToFieldType(v.schema)}</span>
              {v.required && <span className="wf-inputs-row-req">required</span>}
              <button
                type="button"
                className="btn btn-ghost wf-inputs-row-x"
                onClick={(e) => { e.stopPropagation(); remove(i); }}
                title="Remove field"
              >
                <Ic.Trash size={11} />
              </button>
            </li>
          ))}
        </ul>
      )}

      {editing && (
        <AddInputFieldModal
          initial={editing.index != null ? inputs[editing.index] : undefined}
          takenNames={takenNames(editing.index)}
          onSave={upsert}
          onClose={() => setEditing(null)}
        />
      )}
    </section>
  );
}
