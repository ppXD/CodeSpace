import { Ic } from "@/_imported/ai-code-space/icons";

/** One editable model row. `id` (the backend row id) is present for models that already exist. */
export interface ModelRow {
  id?: string;
  modelId: string;
  displayName: string;
  /** The operator-marked default for an "auto" run (existing rows only). */
  isDefault?: boolean;
}

/**
 * A multi-row model editor: one row per model (model-id + display name + delete), with "+ Add model" to
 * append a blank row. Fully controlled — the parent owns the rows and decides how they persist (staged onto
 * a new credential, or reconciled against an existing one). When `onSetDefault` is given, an EXISTING row also
 * shows a star to mark it the default model an "auto" run uses — applied immediately (independent of Save).
 */
export function ModelRowsEditor({ rows, onChange, onSetDefault }: { rows: ModelRow[]; onChange: (rows: ModelRow[]) => void; onSetDefault?: (rowId: string) => void }) {
  const setRow = (i: number, patch: Partial<ModelRow>) => onChange(rows.map((r, idx) => idx === i ? { ...r, ...patch } : r));
  const addRow = () => onChange([...rows, { modelId: "", displayName: "" }]);
  const removeRow = (i: number) => onChange(rows.filter((_, idx) => idx !== i));

  return (
    <div className="mc-modelrows">
      {rows.map((r, i) => (
        <div className="mc-modelrow" key={r.id ?? `new-${i}`}>
          {onSetDefault && r.id && (
            <button
              type="button"
              className={`mc-modelrow-star${r.isDefault ? " is-default" : ""}`}
              title={r.isDefault ? "Default model for auto runs" : "Set as the default model for auto runs"}
              onClick={() => onSetDefault(r.id!)}
            >
              <Ic.Star size={14} fill={r.isDefault ? "currentColor" : "none"} />
            </button>
          )}
          <input className="wf-form-input mc-modelrow-id" value={r.modelId} onChange={e => setRow(i, { modelId: e.target.value })} placeholder="model-id" />
          <input className="wf-form-input" value={r.displayName} onChange={e => setRow(i, { displayName: e.target.value })} placeholder="Display name" />
          <button type="button" className="mc-modelrow-del" title="Remove model" onClick={() => removeRow(i)}><Ic.Trash size={14} /></button>
        </div>
      ))}
      <button type="button" className="mc-addmodel" onClick={addRow}><Ic.Plus size={13} /> Add model</button>
    </div>
  );
}
