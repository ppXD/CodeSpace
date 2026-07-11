import type { ConversationSummary } from "@/api/chat";
import { useConversations } from "@/hooks/use-chat";

import { SearchSelect, type SearchOption } from "./SearchSelect";

/**
 * Single-conversation picker (`"x-selector": "conversation"`). Saves the chosen conversation's UUID.
 * Renders the shared {@link SearchSelect} combobox so it matches every other dropdown.
 */
function toOption(c: ConversationSummary): SearchOption {
  const label = c.kind === "Channel" ? `#${c.slug ?? c.name ?? "channel"}` : (c.name || "(direct message)");
  return { id: c.id, label };
}

export function ConversationSelector({ value, onChange }: { value: string; onChange: (next: string) => void }) {
  const conversations = useConversations();
  const options = (conversations.data ?? []).map(toOption);

  return (
    <SearchSelect
      options={options}
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[0] ?? "")}
      loading={conversations.isLoading}
      placeholder="Pick a conversation…"
    />
  );
}
