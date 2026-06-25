import { useEffect, useMemo, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

export interface FilterOption {
  value: string;
  label: string;
}

/** Show a search box once the option list is longer than this — short lists don't need one. */
const SEARCH_THRESHOLD = 6;

/**
 * A generic MULTI-select filter pill — the building block of the runs filter bar. Closed, it's a compact pill showing
 * the dimension name (e.g. "Repository") and, when armed, a coral COUNT of how many values are picked (so the bar stays
 * lean however many you choose). Open, it's a searchable CHECKBOX list: ticking a row toggles that value and KEEPS the
 * popover open, so you build a set in one pass; the header carries the count + a per-facet Clear. Values within a
 * dimension are OR'd on the wire. `[]` = no constraint. Self-contained — closes on outside-click / Escape, no portal.
 */
export function FilterSelect({ label, options, values, onChange, loading }: {
  label: string;
  options: FilterOption[];
  values: string[];
  onChange: (next: string[]) => void;
  loading?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;

    const onDown = (e: MouseEvent) => { if (!rootRef.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };

    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDown); document.removeEventListener("keydown", onKey); };
  }, [open]);

  const chosen = useMemo(() => new Set(values), [values]);

  const matches = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q === "" ? options : options.filter((o) => o.label.toLowerCase().includes(q));
  }, [options, query]);

  const toggle = (v: string) => onChange(chosen.has(v) ? values.filter((x) => x !== v) : [...values, v]);

  return (
    <div className="filterpill-root" ref={rootRef}>
      <div className="filterpill" data-armed={values.length > 0 ? true : undefined}>
        <button
          type="button"
          className="filterpill-main"
          aria-haspopup="listbox"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
        >
          <span className="filterpill-label">{label}</span>
          {values.length > 0
            ? <span className="filterpill-count">{values.length}</span>
            : <span className="filterpill-caret" aria-hidden="true"><Ic.ChevronDown size={11} /></span>}
        </button>
      </div>

      {open && (
        <div className="filterpop">
          <div className="filterpop-head">
            <span className="filterpop-head-label">{label}</span>
            {values.length > 0 && <span className="filterpop-head-count">{values.length} selected</span>}
            {values.length > 0 && <button type="button" className="filterpop-head-clear" onClick={() => onChange([])}>Clear</button>}
          </div>

          {options.length > SEARCH_THRESHOLD && (
            <input
              className="filterpop-search"
              autoFocus
              value={query}
              placeholder={`Search ${label.toLowerCase()}…`}
              aria-label={`Search ${label}`}
              onChange={(e) => setQuery(e.target.value)}
            />
          )}

          <div className="filterpop-list" role="listbox" aria-label={label} aria-multiselectable="true">
            {loading ? (
              <div className="filterpop-empty">Loading…</div>
            ) : matches.length === 0 ? (
              <div className="filterpop-empty">{query ? "No match." : "Nothing to show."}</div>
            ) : (
              matches.map((o) => {
                const on = chosen.has(o.value);
                return (
                  <button
                    key={o.value}
                    type="button"
                    role="option"
                    aria-selected={on}
                    className="filterpop-opt"
                    data-selected={on || undefined}
                    onClick={() => toggle(o.value)}
                  >
                    <span className="filterpop-box" aria-hidden="true">{on && <Ic.Check size={11} />}</span>
                    <span className="filterpop-opt-label">{o.label}</span>
                  </button>
                );
              })
            )}
          </div>
        </div>
      )}
    </div>
  );
}
