import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useConversations, useCreateChannel } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMemberMap } from "@/hooks/use-team-members";

import { conversationTitle } from "./conversationTitle";

/**
 * Conversation picker for the chat dock: the team's conversations + an inline "new channel"
 * form. Selection is a callback (not a route) so picking a conversation swaps the dock's pane
 * in place — no navigation, so you never leave whatever you're looking at. Channel slug is
 * normalised server-side, so the form sends the typed name as both name and slug.
 */
export function ConversationList({ activeConversationId, onSelect }: { activeConversationId: string | null; onSelect: (conversationId: string) => void }) {
  const conversations = useConversations();
  const create = useCreateChannel();
  const members = useTeamMemberMap();
  const me = useMe();

  const [adding, setAdding] = useState(false);
  const [name, setName] = useState("");

  const submit = async () => {
    const trimmed = name.trim();
    if (!trimmed || create.isPending) return;

    const created = await create.mutateAsync({ name: trimmed, slug: trimmed });
    setName("");
    setAdding(false);
    onSelect(created.id);   // jump straight into the channel you just made
  };

  const rows = conversations.data ?? [];

  return (
    <div className="chat-list">
      <div className="chat-list-head">
        <span className="chat-list-title">Conversations</span>
        <button className="chrome-btn" title="New channel" onClick={() => setAdding(a => !a)}>
          <Ic.Plus size={14} />
        </button>
      </div>

      {adding && (
        <div className="chat-newchannel">
          <input
            className="chat-newchannel-input"
            value={name}
            autoFocus
            placeholder="channel name"
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") submit();
              if (e.key === "Escape") setAdding(false);
            }}
          />
          <button className="btn btn-primary" onClick={submit} disabled={create.isPending || name.trim().length === 0}>Add</button>
        </div>
      )}

      <div className="chat-list-body">
        {conversations.isLoading && <div className="chat-empty">Loading…</div>}

        {!conversations.isLoading && rows.length === 0 && (
          <div className="chat-empty">No conversations yet. Create a channel to start.</div>
        )}

        {rows.map(c => (
          <button
            key={c.id}
            type="button"
            className="chat-conv"
            data-active={c.id === activeConversationId}
            onClick={() => onSelect(c.id)}
          >
            <span className="chat-conv-icon">{c.kind === "Channel" ? "#" : <Ic.Chat size={13} />}</span>
            <span className="chat-conv-name">{conversationTitle(c, members, me.data?.id ?? null)}</span>
          </button>
        ))}
      </div>
    </div>
  );
}
