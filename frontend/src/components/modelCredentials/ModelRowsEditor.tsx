import { Ic } from "@/_imported/ai-code-space/icons";

/** One editable model row. `id` (the backend row id) is present for models that already exist. */
export interface ModelRow {
  id?: string;
  modelId: string;
  displayName: string;
  /** The operator-marked default for an "auto" run (existing rows only). */
  isDefault?: boolean;
  /** USD per 1M input tokens, as typed. Blank = unpriced — a run with a cost cap cannot spend on this model. */
  inputUsdPerMillion?: string;
  /** USD per 1M output tokens, as typed. Blank = unpriced. */
  outputUsdPerMillion?: string;
}

/**
 * A multi-row model editor: one row per model (model-id + display name + $/M in + $/M out + delete), with
 * "+ Add model" to append a blank row. Fully controlled — the parent owns the rows and decides how they persist
 * (staged onto a new credential, or reconciled against an existing one). When `onSetDefault` is given, an EXISTING
 * row also shows a star to mark it the default model an "auto" run uses — applied immediately (independent of Save).
 *
 * The two price fields are what make a run's cost cap enforceable for this model: the built-in price table only
 * knows a handful of vendor ids, and a capped run refuses to spend on a model nobody can price. An existing row's
 * price is committed on blur (like the star); a brand-new row's rides along with its Save.
 */
export function ModelRowsEditor({ rows, onChange, onSetDefault, onSetPrice }: { rows: ModelRow[]; onChange: (rows: ModelRow[]) => void; onSetDefault?: (rowId: string) => void; onSetPrice?: (rowId: string, row: ModelRow) => void }) {
  const setRow = (i: number, patch: Partial<ModelRow>) => onChange(rows.map((r, idx) => idx === i ? { ...r, ...patch } : r));
  const addRow = () => onChange([...rows, { modelId: "", displayName: "" }]);
  const removeRow = (i: number) => onChange(rows.filter((_, idx) => idx !== i));

  return (
    <div className="mc-modelrows">
      {rows.map((r, i) => {
        const unpriced = !(r.inputUsdPerMillion ?? "").trim() && !(r.outputUsdPerMillion ?? "").trim();

        return (
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
            <input
              className={`wf-form-input mc-modelrow-price${unpriced ? " is-unpriced" : ""}`}
              value={r.inputUsdPerMillion ?? ""}
              onChange={e => setRow(i, { inputUsdPerMillion: e.target.value })}
              onBlur={() => r.id && onSetPrice?.(r.id, rows[i])}
              inputMode="decimal"
              placeholder="$/M in"
              aria-label={`Price per million input tokens for ${r.modelId || "this model"}`}
              title={unpriced ? "Unpriced — a run with a cost cap cannot spend on this model" : "USD per 1M input tokens"}
            />
            <input
              className={`wf-form-input mc-modelrow-price${unpriced ? " is-unpriced" : ""}`}
              value={r.outputUsdPerMillion ?? ""}
              onChange={e => setRow(i, { outputUsdPerMillion: e.target.value })}
              onBlur={() => r.id && onSetPrice?.(r.id, rows[i])}
              inputMode="decimal"
              placeholder="$/M out"
              aria-label={`Price per million output tokens for ${r.modelId || "this model"}`}
              title={unpriced ? "Unpriced — a run with a cost cap cannot spend on this model" : "USD per 1M output tokens"}
            />
            <button type="button" className="mc-modelrow-del" title="Remove model" onClick={() => removeRow(i)}><Ic.Trash size={14} /></button>
          </div>
        );
      })}
      <button type="button" className="mc-addmodel" onClick={addRow}><Ic.Plus size={13} /> Add model</button>
    </div>
  );
}
