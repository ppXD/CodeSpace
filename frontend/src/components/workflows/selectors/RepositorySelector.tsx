import { useMemo, useRef, useState, useEffect } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useRepositories } from "@/hooks/use-repositories";

/**
 * Picker for one team-scoped repository UUID. Renders a button that opens a searchable
 * popover listing every repository the current team has bound. Selection writes the repo's
 * UUID into the form value — the on-disk workflow definition stays a plain string so the
 * engine doesn't need to know anything about UI affordances.
 *
 * Used by the schema-driven form whenever a config field declares <c>"x-selector":
 * "repository"</c>. Falling back to a freeform UUID text input is supported by closing the
 * popover and typing into the search box — the picker doesn't intercept paste, so an
 * operator who has a UUID in their clipboard can still drive the field directly.
 */

interface RepositorySelectorProps {
  /** Current UUID string. Empty = "any repository" — caller decides whether to label it. */
  value: string;
  onChange: (next: string) => void;
  placeholder?: string;
}

export function RepositorySelector({ value, onChange, placeholder }: RepositorySelectorProps) {
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const containerRef = useRef<HTMLDivElement>(null);

  const repos = useRepositories();
  const rows = useMemo(() => repos.data ?? [], [repos.data]);

  const selected = useMemo(() => rows.find((r) => r.id === value), [rows, value]);

  const filtered = useMemo(() => {
    if (!filter) return rows;
    const f = filter.toLowerCase();
    return rows.filter((r) =>
      r.fullPath.toLowerCase().includes(f)
      || r.name.toLowerCase().includes(f)
      || r.id.toLowerCase().includes(f),
    );
  }, [rows, filter]);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener("mousedown", handler);
    return () => window.removeEventListener("mousedown", handler);
  }, [open]);

  const pick = (id: string) => {
    onChange(id);
    setOpen(false);
    setFilter("");
  };

  const clear = (e: React.MouseEvent) => {
    e.stopPropagation();
    onChange("");
  };

  return (
    <div className="wf-selector wf-selector-repo" ref={containerRef}>
      <button
        type="button"
        className="wf-selector-trigger"
        onClick={() => setOpen((o) => !o)}
        title={selected?.fullPath ?? "Pick a repository"}
      >
        {selected ? (
          <>
            <Ic.Repo size={12} />
            <span className="wf-selector-trigger-label">{selected.fullPath}</span>
            <span className="wf-selector-trigger-id" title={selected.id}>{shortId(selected.id)}</span>
          </>
        ) : value ? (
          <>
            <Ic.Repo size={12} />
            <span className="wf-selector-trigger-label wf-selector-trigger-unknown">
              Unknown repo · {shortId(value)}
            </span>
          </>
        ) : (
          <>
            <Ic.Repo size={12} />
            <span className="wf-selector-trigger-label wf-selector-trigger-placeholder">
              {placeholder ?? "Any repository — pick one to scope the trigger"}
            </span>
          </>
        )}
        <span className="wf-selector-trigger-actions">
          {value && (
            <span
              role="button"
              tabIndex={0}
              className="wf-selector-clear"
              onClick={clear}
              onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onChange(""); } }}
              title="Clear (matches any repository)"
            ><Ic.X size={10} /></span>
          )}
          <Ic.ChevronDown size={11} />
        </span>
      </button>

      {open && (
        <div className="wf-selector-popover" role="listbox">
          <input
            autoFocus
            type="text"
            className="wf-selector-search"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter by path, name, or id…"
          />
          {repos.isLoading && (
            <div className="wf-selector-empty">Loading repositories…</div>
          )}
          {!repos.isLoading && rows.length === 0 && (
            <div className="wf-selector-empty">
              No repositories in this team yet. Add one from the Repositories page first.
            </div>
          )}
          {!repos.isLoading && rows.length > 0 && filtered.length === 0 && (
            <div className="wf-selector-empty">No matches.</div>
          )}
          {filtered.length > 0 && (
            <ul className="wf-selector-list">
              {filtered.map((r) => (
                <li
                  key={r.id}
                  role="option"
                  aria-selected={r.id === value}
                  className="wf-selector-item"
                  data-selected={r.id === value ? "true" : undefined}
                  onMouseDown={(e) => { e.preventDefault(); pick(r.id); }}
                >
                  <Ic.Repo size={11} />
                  <span className="wf-selector-item-body">
                    <span className="wf-selector-item-path">{r.fullPath}</span>
                    <span className="wf-selector-item-id">{shortId(r.id)}</span>
                  </span>
                  {r.id === value && <Ic.Check size={11} />}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

function shortId(id: string): string {
  // First chunk of a UUID — long enough to be unique within a typical team's repo
  // list while staying out of the way visually.
  const dash = id.indexOf("-");
  return dash > 0 ? id.slice(0, dash) : id.slice(0, 8);
}
