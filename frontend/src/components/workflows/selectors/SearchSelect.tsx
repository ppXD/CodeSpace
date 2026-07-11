import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

/**
 * One pickable entity. `label` is the row text; `meta` is a dimmed disambiguator (e.g. a provider or
 * handle); `avatar` is an optional 1-char tag. `id` is what gets stored.
 */
export interface SearchOption {
  id: string;
  label: string;
  meta?: string;
  avatar?: { text: string; bg?: string; fg?: string };
}

interface SearchSelectProps {
  options: SearchOption[];
  /** Selected ids. Single mode uses 0 or 1; multi uses any number. */
  value: string[];
  onChange: (next: string[]) => void;
  multi?: boolean;
  loading?: boolean;
  /** Placeholder for the search input when nothing is chosen. */
  placeholder?: string;
  /** Help line under the control (e.g. multi-select "empty = all"). Omit for none. */
  hint?: string;
}

/** How many filtered candidates the dropdown shows before asking the operator to keep typing. */
const MAX_VISIBLE = 8;

/**
 * The one searchable-combobox picker used across the workflow editor — single OR multi — so every dropdown
 * (model, agent, repo, conversation, harness, …) reads and behaves the same. Selected items render as
 * removable chips; an inline search filters the rest; the popover is keyboard-navigable (arrow / enter /
 * backspace / escape) and capped at {@link MAX_VISIBLE}. A saved id no longer in `options` stays visible as
 * a flagged chip so the field never silently blanks. The stored value is unchanged (an id / array of ids).
 */
export function SearchSelect({ options, value, onChange, multi = false, loading = false, placeholder = "Search…", hint }: SearchSelectProps) {
  const byId = useMemo(() => new Map(options.map((o) => [o.id, o])), [options]);

  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);

  const matches = useMemo(() => {
    const chosen = new Set(value);
    const q = query.trim().toLowerCase();
    return options.filter((o) => !chosen.has(o.id) && (q === "" ||
      o.label.toLowerCase().includes(q) || (o.meta ?? "").toLowerCase().includes(q)));
  }, [options, value, query]);

  const visible = matches.slice(0, MAX_VISIBLE);
  const overflow = matches.length - visible.length;

  const pick = (id: string) => {
    onChange(multi ? (value.includes(id) ? value : [...value, id]) : [id]);
    setQuery("");
    setActiveIndex(0);
    if (!multi) setOpen(false);
  };

  const remove = (id: string) => onChange(value.filter((x) => x !== id));

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") { e.preventDefault(); setOpen(true); setActiveIndex((i) => Math.min(i + 1, visible.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActiveIndex((i) => Math.max(i - 1, 0)); }
    else if (e.key === "Enter" && open && visible[activeIndex]) { e.preventDefault(); pick(visible[activeIndex].id); }
    else if (e.key === "Escape") { setOpen(false); }
    else if (e.key === "Backspace" && query === "" && value.length > 0) { remove(value[value.length - 1]!); }
  };

  return (
    <div className="wf-userpick">
      <div className="wf-userpick-control">
        {value.map((id) => {
          const opt = byId.get(id);
          const label = opt ? opt.label : "Unavailable";
          const av = opt?.avatar;

          return (
            <span key={id} className="wf-userpick-tag">
              {av && <span className="wf-userpick-tag-av" style={{ background: av.bg, color: av.fg }}>{av.text}</span>}
              <span className="wf-userpick-tag-name">{label}</span>
              <button type="button" className="wf-userpick-tag-x" aria-label={`Remove ${label}`} onClick={() => remove(id)}><Ic.X size={8} /></button>
            </span>
          );
        })}

        {/* The input is ALWAYS present — in single mode you click it to switch to another option without
            removing the current one first (picking replaces). Multi keeps it to add more. */}
        <input
          type="text"
          className="wf-userpick-input"
          value={query}
          placeholder={value.length === 0 ? placeholder : (multi ? "Add another…" : "Change…")}
          aria-label={placeholder}
          onChange={(e) => { setQuery(e.target.value); setOpen(true); setActiveIndex(0); }}
          onFocus={() => setOpen(true)}
          onBlur={() => setOpen(false)}
          onKeyDown={onKeyDown}
        />
      </div>

      {open && (
        <div className="wf-userpick-pop" role="listbox">
          {loading ? (
            <div className="wf-userpick-empty">Loading…</div>
          ) : visible.length === 0 ? (
            <div className="wf-userpick-empty">{query ? "No match." : "Nothing to add."}</div>
          ) : (
            <>
              {visible.map((o, i) => (
                <button
                  key={o.id}
                  type="button"
                  role="option"
                  aria-selected={i === activeIndex}
                  // Explicit name — the option renders its label + meta as spans, but a surrounding <label>
                  // (some fields wrap the picker in one) would otherwise capture the button's accessible name.
                  aria-label={o.meta ? `${o.label} · ${o.meta}` : o.label}
                  className="wf-userpick-opt"
                  data-active={i === activeIndex}
                  onMouseDown={(e) => { e.preventDefault(); pick(o.id); }}
                  onMouseEnter={() => setActiveIndex(i)}
                >
                  {o.avatar && <span className="wf-userpick-opt-av" style={{ background: o.avatar.bg, color: o.avatar.fg }}>{o.avatar.text}</span>}
                  <span className="wf-userpick-opt-name">{o.label}{o.meta ? <span className="wf-userpick-opt-meta"> · {o.meta}</span> : null}</span>
                </button>
              ))}
              {overflow > 0 && <div className="wf-userpick-more">+{overflow} more — keep typing to filter</div>}
            </>
          )}
        </div>
      )}

      {hint && <span className="wf-form-help">{hint}</span>}
    </div>
  );
}
