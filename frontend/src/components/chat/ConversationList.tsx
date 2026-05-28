import { Link } from "@tanstack/react-router";
import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { useConversations, useCreateChannel } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMemberMap } from "@/hooks/use-team-members";

import { conversationTitle } from "./conversationTitle";

/**
 * Left rail: the team's conversations + an inline "new channel" form. Each row deep-links to
 * <c>/teams/{slug}/chat/{conversationId}</c> so a conversation is shareable / back-button-able.
 * Channel slug is normalised server-side, so the create form sends the typed name as both name
 * and slug and lets the backend canonicalise.
 */
export function ConversationList({ teamSlug, activeConversationId }: { teamSlug: string; activeConversationId: string | null }) {
  const conversations = useConversations();
  const create = useCreateChannel();
  const members = useTeamMemberMap();
  const me = useMe();

  const [adding, setAdding] = useState(false);
  const [name, setName] = useState("");

  const submit = async () => {
    const trimmed = name.trim();
    if (!trimmed || create.isPending) return;

    await create.mutateAsync({ name: trimmed, slug: trimmed });
    setName("");
    setAdding(false);
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
          <Link
            key={c.id}
            to="/teams/$teamSlug/chat/$conversationId"
            params={{ teamSlug, conversationId: c.id }}
            className="chat-conv"
            data-active={c.id === activeConversationId}
          >
            <span className="chat-conv-icon">{c.kind === "Channel" ? "#" : <Ic.Chat size={13} />}</span>
            <span className="chat-conv-name">{conversationTitle(c, members, me.data?.id ?? null)}</span>
          </Link>
        ))}
      </div>
    </div>
  );
}
