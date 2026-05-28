import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { ConversationSummary } from "@/api/chat";
import { useConversations, useCreateChannel } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMemberMap } from "@/hooks/use-team-members";

import { conversationTitle } from "./conversationTitle";

/**
 * A selectable conversation list for the chat rail. Selection is a callback (not a route) so a
 * pick swaps the centre conversation view in place — no navigation. Optionally filtered (e.g.
 * the Channels tab passes `kind === "Channel"`) and optionally shows an inline channel creator.
 */
export function ConversationList({
  activeConversationId,
  onSelect,
  filter,
  showCreate = false,
}: {
  activeConversationId: string | null;
  onSelect: (conversationId: string) => void;
  filter?: (conversation: ConversationSummary) => boolean;
  showCreate?: boolean;
}) {
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
    onSelect(created.id);   // open the channel you just made
  };

  const rows = (conversations.data ?? []).filter(c => (filter ? filter(c) : true));

  return (
    <div className="chat-list">
      {showCreate && (
        <div className="chat-list-actions">
          {adding ? (
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
          ) : (
            <button className="chat-newchannel-trigger" onClick={() => setAdding(true)}>
              <Ic.Plus size={13} /> New channel
            </button>
          )}
        </div>
      )}

      <div className="chat-list-body">
        {conversations.isLoading && <div className="chat-empty">Loading…</div>}

        {!conversations.isLoading && rows.length === 0 && (
          <div className="chat-empty">Nothing here yet.</div>
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
