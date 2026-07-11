import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useCredentialedModels, type CredentialedModelOption } from "@/hooks/use-model-credentials";

/**
 * Pickers for a `"x-selector": "credentialedModel"` field. The saved value is the credentialed-model
 * ROW id (`CredentialedModelOption.rowId`) — the (credential, model) handle the backend's model pool
 * resolves by (`ResolveByRowIdAsync`), NOT the bare model id or the credential id. Two credentials that
 * expose the same model name are distinct rows, so the row id is the only unambiguous handle.
 *
 * Used by the supervisor's brain (`supervisorModelId`), the plan/decision reviewer (`reviewerModelId`),
 * and the dispatch allow-list (`allowedModelIds[]`). A scalar field also gets SchemaForm's Pick ⇄
 * Expression toggle, so it can be bound to a `{{ }}` reference instead.
 */

/** One human option label: `model · credential (provider)`, with an offline hint when unreachable. */
function optionLabel(m: CredentialedModelOption): string {
  const base = `${m.modelId} · ${m.credentialName} (${m.provider})`;
  return m.available === false ? `${base} — offline` : base;
}

/**
 * Single credentialed-model picker. Value = the chosen model's `rowId`. A saved row that's no longer in
 * the team's pool stays visible (flagged "unavailable") so the field never silently blanks — resolution
 * would reject it anyway, but the operator sees it needs re-picking.
 */
export function CredentialedModelSelector({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  const models = useCredentialedModels();
  const rows = models.data ?? [];

  const stale = value && !rows.some((m) => m.rowId === value);

  return (
    <select className="wf-form-input" value={value} onChange={(e) => onChange(e.target.value)} aria-label="Model">
      <option value="">{models.isLoading ? "Loading…" : "Pick a model…"}</option>
      {rows.map((m) => <option key={m.rowId} value={m.rowId}>{optionLabel(m)}</option>)}
      {stale && <option value={value}>Saved model — unavailable</option>}
    </select>
  );
}

/** How many filtered candidates the dropdown shows before asking the operator to keep typing. */
const MAX_VISIBLE = 8;

/**
 * Multi credentialed-model picker — a SEARCHABLE combobox whose value is an array of `rowId`s. Drives an
 * `array` `"x-selector": "credentialedModel"` field (e.g. the supervisor's `allowedModelIds`). Selected
 * models render as removable tags; an inline search filters the rest by model / credential / provider.
 * Empty selection is meaningful where the consumer treats "none chosen" as "any is allowed" — the hint
 * says so. Mirrors UserMultiSelector's interaction (arrow/enter/backspace, capped dropdown).
 */
export function CredentialedModelMultiSelector({ value, onChange }: { value: string[]; onChange: (next: string[]) => void }) {
  const models = useCredentialedModels();
  const rows = useMemo(() => models.data ?? [], [models.data]);
  const byId = useMemo(() => new Map(rows.map((m) => [m.rowId, m])), [rows]);

  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);

  // Not-yet-selected models matching the query (by model id, credential, or provider); empty query = all.
  const matches = useMemo(() => {
    const chosen = new Set(value);
    const q = query.trim().toLowerCase();
    return rows.filter((m) => !chosen.has(m.rowId) && (q === "" ||
      m.modelId.toLowerCase().includes(q) || m.credentialName.toLowerCase().includes(q) || m.provider.toLowerCase().includes(q)));
  }, [rows, value, query]);

  const visible = matches.slice(0, MAX_VISIBLE);
  const overflow = matches.length - visible.length;

  const add = (id: string) => {
    if (!value.includes(id)) onChange([...value, id]);
    setQuery("");
    setActiveIndex(0);
  };

  const remove = (id: string) => onChange(value.filter((x) => x !== id));

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") { e.preventDefault(); setOpen(true); setActiveIndex((i) => Math.min(i + 1, visible.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActiveIndex((i) => Math.max(i - 1, 0)); }
    else if (e.key === "Enter" && open && visible[activeIndex]) { e.preventDefault(); add(visible[activeIndex].rowId); }
    else if (e.key === "Escape") { setOpen(false); }
    else if (e.key === "Backspace" && query === "" && value.length > 0) { remove(value[value.length - 1]!); }
  };

  return (
    <div className="wf-userpick">
      <div className="wf-userpick-control">
        {value.map((id) => {
          const m = byId.get(id);
          const label = m ? `${m.modelId} · ${m.credentialName}` : "Unavailable model";

          return (
            <span key={id} className="wf-userpick-tag">
              <span className="wf-userpick-tag-name">{label}</span>
              <button type="button" className="wf-userpick-tag-x" aria-label={`Remove ${label}`} onClick={() => remove(id)}><Ic.X size={8} /></button>
            </span>
          );
        })}

        <input
          type="text"
          className="wf-userpick-input"
          value={query}
          placeholder={value.length === 0 ? "Search models…" : "Add another…"}
          aria-label="Search models"
          onChange={(e) => { setQuery(e.target.value); setOpen(true); setActiveIndex(0); }}
          onFocus={() => setOpen(true)}
          onBlur={() => setOpen(false)}
          onKeyDown={onKeyDown}
        />
      </div>

      {open && (
        <div className="wf-userpick-pop" role="listbox">
          {models.isLoading ? (
            <div className="wf-userpick-empty">Loading models…</div>
          ) : visible.length === 0 ? (
            <div className="wf-userpick-empty">{query ? "No matching model." : "Every model's already added."}</div>
          ) : (
            <>
              {visible.map((m, i) => (
                <button
                  key={m.rowId}
                  type="button"
                  role="option"
                  aria-selected={i === activeIndex}
                  className="wf-userpick-opt"
                  data-active={i === activeIndex}
                  onMouseDown={(e) => { e.preventDefault(); add(m.rowId); }}
                  onMouseEnter={() => setActiveIndex(i)}
                >
                  <span className="wf-userpick-opt-name">{optionLabel(m)}</span>
                </button>
              ))}
              {overflow > 0 && <div className="wf-userpick-more">+{overflow} more — keep typing to filter</div>}
            </>
          )}
        </div>
      )}

      <span className="wf-form-help">
        {value.length === 0 ? "None selected — any of the team's models may be used." : `${value.length} selected — dispatched agents must use one of these.`}
      </span>
    </div>
  );
}
