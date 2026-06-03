import { useMemo, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { avatarColor } from "@/lib/avatarColor";
import { useTeamMembers } from "@/hooks/use-team-members";

/**
 * Single-user picker. Lists the team's pickable members (bot-excluded); the saved value is the chosen
 * user's UUID. Used by the schema-driven form for a scalar field with `"x-selector": "user"` — generic,
 * not tied to any node. A scalar `user` field also gets the Pick ⇄ Expression toggle from SchemaForm,
 * so it can be bound to a `{{ }}` reference instead.
 */
export function UserSelector({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  const members = useTeamMembers();
  const rows = members.data ?? [];

  return (
    <select className="wf-form-input" value={value} onChange={(e) => onChange(e.target.value)} aria-label="User">
      <option value="">{members.isLoading ? "Loading…" : "Pick a user…"}</option>
      {rows.map((m) => <option key={m.userId} value={m.userId}>{m.name}</option>)}
    </select>
  );
}

/** How many filtered candidates the dropdown shows before asking the user to keep typing. */
const MAX_VISIBLE = 8;

/**
 * Multi-user picker — a SEARCHABLE combobox whose value is an array of user UUIDs. Drives any
 * `"x-selector": "user"` field of type `array` (e.g. chat.post_message's allowedResponderUserIds).
 *
 * Selected members render as removable tags; an inline search input filters the rest. The candidate
 * dropdown only appears while focused and is capped at {@link MAX_VISIBLE} (with a "+N more" hint), so a
 * team of 5 or 500 stays a compact control instead of rendering one chip per member. Empty selection is
 * meaningful where the consumer treats "no one chosen" as "anyone" — the hint says so.
 */
export function UserMultiSelector({ value, onChange }: { value: string[]; onChange: (next: string[]) => void }) {
  const members = useTeamMembers();
  const rows = useMemo(() => members.data ?? [], [members.data]);
  const byId = useMemo(() => new Map(rows.map((m) => [m.userId, m])), [rows]);

  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);

  // Not-yet-selected members matching the query (by name or email); empty query = all of them.
  const matches = useMemo(() => {
    const chosen = new Set(value);
    const q = query.trim().toLowerCase();
    return rows.filter((m) => !chosen.has(m.userId) && (q === "" || m.name.toLowerCase().includes(q) || (m.email ?? "").toLowerCase().includes(q)));
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
    else if (e.key === "Enter" && open && visible[activeIndex]) { e.preventDefault(); add(visible[activeIndex].userId); }
    else if (e.key === "Escape") { setOpen(false); }
    else if (e.key === "Backspace" && query === "" && value.length > 0) { remove(value[value.length - 1]!); }
  };

  return (
    <div className="wf-userpick">
      <div className="wf-userpick-control">
        {value.map((id) => {
          const label = byId.get(id)?.name ?? "Unknown member";
          const color = avatarColor(id);

          return (
            <span key={id} className="wf-userpick-tag">
              <span className="wf-userpick-tag-av" style={{ background: color.bg, color: color.fg }}>{label.charAt(0).toUpperCase()}</span>
              <span className="wf-userpick-tag-name">{label}</span>
              <button type="button" className="wf-userpick-tag-x" aria-label={`Remove ${label}`} onClick={() => remove(id)}><Ic.X size={8} /></button>
            </span>
          );
        })}

        <input
          type="text"
          className="wf-userpick-input"
          value={query}
          placeholder={value.length === 0 ? "Search members…" : "Add another…"}
          aria-label="Search members"
          onChange={(e) => { setQuery(e.target.value); setOpen(true); setActiveIndex(0); }}
          onFocus={() => setOpen(true)}
          onBlur={() => setOpen(false)}
          onKeyDown={onKeyDown}
        />
      </div>

      {open && (
        <div className="wf-userpick-pop" role="listbox">
          {members.isLoading ? (
            <div className="wf-userpick-empty">Loading members…</div>
          ) : visible.length === 0 ? (
            <div className="wf-userpick-empty">{query ? "No matching member." : "Everyone's already added."}</div>
          ) : (
            <>
              {visible.map((m, i) => {
                const color = avatarColor(m.userId);

                return (
                  <button
                    key={m.userId}
                    type="button"
                    role="option"
                    aria-selected={i === activeIndex}
                    className="wf-userpick-opt"
                    data-active={i === activeIndex}
                    onMouseDown={(e) => { e.preventDefault(); add(m.userId); }}
                    onMouseEnter={() => setActiveIndex(i)}
                  >
                    <span className="wf-userpick-opt-av" style={{ background: color.bg, color: color.fg }}>{m.name.charAt(0).toUpperCase()}</span>
                    <span className="wf-userpick-opt-name">{m.name}</span>
                  </button>
                );
              })}
              {overflow > 0 && <div className="wf-userpick-more">+{overflow} more — keep typing to filter</div>}
            </>
          )}
        </div>
      )}

      <span className="wf-form-help">
        {value.length === 0 ? "None selected — anyone in the conversation can respond." : `${value.length} selected — only these can respond.`}
      </span>
    </div>
  );
}
