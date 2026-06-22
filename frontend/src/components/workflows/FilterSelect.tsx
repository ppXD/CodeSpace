import { useEffect, useMemo, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

export interface FilterOption {
  value: string;
  label: string;
}

/** Show a search box once the option list is longer than this — short lists don't need one. */
const SEARCH_THRESHOLD = 6;

/**
 * A generic single-select filter pill — the building block of the runs filter bar. Closed, it's a compact pill showing
 * the dimension name (e.g. "Repository"), or, when armed, the chosen option with an inline clear (✕). Open, it's a
 * searchable option list. Single-select by design: the backend takes a list per dimension, but the v1 bar keeps each
 * to one value (so picking one closes the popover); multi-select is a later, additive change. `null` value = no
 * constraint on this dimension. The control is self-contained — it closes on outside-click / Escape, no portal.
 */
export function FilterSelect({ label, options, value, onChange, loading }: {
  label: string;
  options: FilterOption[];
  value: string | null;
  onChange: (next: string | null) => void;
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

  const selected = useMemo(() => options.find((o) => o.value === value) ?? null, [options, value]);

  const matches = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q === "" ? options : options.filter((o) => o.label.toLowerCase().includes(q));
  }, [options, query]);

  const choose = (next: string | null) => { onChange(next); setOpen(false); setQuery(""); };

  return (
    <div className="filterpill-root" ref={rootRef}>
      {/* The trigger and the clear (✕) are SEPARATE sibling buttons — never a button nested in a button (invalid
          HTML + a keyboard-dead control). The container div carries the pill chrome (border / armed tint). */}
      <div className="filterpill" data-armed={selected ? true : undefined}>
        <button
          type="button"
          className="filterpill-main"
          aria-haspopup="listbox"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
        >
          <span className="filterpill-label">{label}</span>
          {selected && <span className="filterpill-value">{selected.label}</span>}
          {!selected && <span className="filterpill-caret" aria-hidden="true"><Ic.ChevronDown size={11} /></span>}
        </button>

        {selected && (
          <button type="button" className="filterpill-x" aria-label={`Clear ${label}`} onClick={() => choose(null)}>
            <Ic.X size={9} />
          </button>
        )}
      </div>

      {open && (
        <div className="filterpop">
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

          <div className="filterpop-list" role="listbox" aria-label={label}>
            {loading ? (
              <div className="filterpop-empty">Loading…</div>
            ) : matches.length === 0 ? (
              <div className="filterpop-empty">{query ? "No match." : "Nothing to show."}</div>
            ) : (
              matches.map((o) => (
                <button
                  key={o.value}
                  type="button"
                  role="option"
                  aria-selected={o.value === value}
                  className="filterpop-opt"
                  data-selected={o.value === value || undefined}
                  onClick={() => choose(o.value)}
                >
                  <span className="filterpop-opt-label">{o.label}</span>
                  {o.value === value && <Ic.Check size={12} aria-hidden="true" />}
                </button>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}
