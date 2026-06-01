import type { ConversationSummary } from "@/api/chat";
import { useConversations } from "@/hooks/use-chat";

interface ConversationSelectorProps {
  /** Selected conversation UUID ("" = none chosen yet). */
  value: string;
  onChange: (next: string) => void;
}

/**
 * Single-conversation picker. Lists the team's conversations; the saved value is the chosen
 * conversation's UUID, which flows downstream as a plain `{{input.<name>}}` string.
 *
 * Used by the schema-driven form whenever a field declares `"x-selector": "conversation"` — generic,
 * not tied to any one node (e.g. a chat-posting node can let the author pick the target channel).
 */
export function ConversationSelector({ value, onChange }: ConversationSelectorProps) {
  const conversations = useConversations();
  const rows = conversations.data ?? [];

  return (
    <select
      className="wf-form-input"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Conversation"
    >
      <option value="">{conversations.isLoading ? "Loading…" : "Pick a conversation…"}</option>
      {rows.map((c) => <option key={c.id} value={c.id}>{conversationLabel(c)}</option>)}
    </select>
  );
}

/** Channel → `#slug`; named group → its name; otherwise a generic DM label. */
function conversationLabel(c: ConversationSummary): string {
  if (c.kind === "Channel") return `#${c.slug ?? c.name ?? "channel"}`;
  if (c.name) return c.name;
  return "(direct message)";
}
