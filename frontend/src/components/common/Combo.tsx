import { useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";

import { usePopover } from "./usePopover";

/** A selectable option for {@link Combo}. `desc` renders as a dim second line in the menu. */
export interface Option { value: string; label: string; desc?: string }

/**
 * In-house warm-theme dropdown (no native &lt;select&gt;). With `label`, renders as a compact settings ROW
 * (label · value · ›); without, a boxed select / pill. Optional top search for long lists. The single warm
 * dropdown shared across the launch composer + the agent editor.
 */
export function Combo({ label, value, options, onChange, placeholder, searchable, buttonClassName }: {
  label?: string;
  value: string;
  options: Option[];
  onChange: (v: string) => void;
  placeholder?: string;
  searchable?: boolean;
  buttonClassName?: string;
}) {
  const { open, setOpen, btnRef, popRef, pos } = usePopover();
  const [q, setQ] = useState("");
  const sel = options.find(o => o.value === value);
  const filtered = searchable && q.trim() ? options.filter(o => o.label.toLowerCase().includes(q.trim().toLowerCase())) : options;
  const isRow = label !== undefined;
  return (
    <>
      <button ref={btnRef} type="button" className={buttonClassName ?? (isRow ? "lt3-srow" : "lt3-combo-btn")} data-open={open} onClick={() => { setQ(""); setOpen(v => !v); }}>
        {isRow && <span className="lt3-srow-l">{label}</span>}
        <span className="lt3-combo-v">{sel?.label ?? placeholder ?? "Select"}</span>
        {isRow ? <Ic.ChevronRight size={15} /> : <Ic.ChevronDown size={14} />}
      </button>
      {open && pos && createPortal(
        <div ref={popRef} className="lt3-pop lt3-combo-pop" style={{ position: "fixed", left: pos.left, top: pos.top, minWidth: pos.width }}>
          {searchable && <input className="lt3-search" placeholder="Search" value={q} onChange={e => setQ(e.target.value)} autoFocus />}
          <div className="lt3-combo-list">
            {filtered.map(o => (
              <button key={o.value} type="button" className="lt3-opt" data-on={o.value === value} onClick={() => { onChange(o.value); setOpen(false); setQ(""); }}>
                <span className="lt3-opt-m"><span className="lt3-opt-t">{o.label}</span>{o.desc && <span className="lt3-opt-d">{o.desc}</span>}</span>
                {o.value === value && <Ic.Check size={14} />}
              </button>
            ))}
            {filtered.length === 0 && <div className="lt3-rempty">No matches</div>}
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}
