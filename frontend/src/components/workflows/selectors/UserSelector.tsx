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

/**
 * Multi-user picker — a toggle-chip list whose value is an array of user UUIDs. Drives any
 * `"x-selector": "user"` field of type `array` (e.g. chat.post_message's allowedResponderUserIds),
 * replacing the raw-GUID text entry. Empty selection is meaningful where the consumer treats "no one
 * chosen" as "anyone" — the hint says so; the consumer's own semantics decide.
 */
export function UserMultiSelector({ value, onChange }: { value: string[]; onChange: (next: string[]) => void }) {
  const members = useTeamMembers();
  const rows = members.data ?? [];
  const selected = new Set(value);

  const toggle = (id: string) => {
    const next = new Set(selected);
    if (!next.delete(id)) next.add(id);
    onChange([...next]);
  };

  return (
    <div className="wf-userpick">
      {members.isLoading ? (
        <span className="wf-form-help">Loading members…</span>
      ) : rows.length === 0 ? (
        <span className="wf-form-help">No members to pick.</span>
      ) : (
        <div className="wf-userpick-chips">
          {rows.map((m) => (
            <button
              key={m.userId}
              type="button"
              className={`wf-userpick-chip${selected.has(m.userId) ? " is-on" : ""}`}
              aria-pressed={selected.has(m.userId)}
              onClick={() => toggle(m.userId)}
            >
              {m.name}
            </button>
          ))}
        </div>
      )}
      <span className="wf-form-help">
        {value.length === 0 ? "None selected — anyone in the conversation can respond." : `${value.length} selected — only these can respond.`}
      </span>
    </div>
  );
}
