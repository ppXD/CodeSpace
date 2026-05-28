import { Ic } from "@/_imported/ai-code-space/icons";
import type { ConversationSummary } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";
import { useConversations } from "@/hooks/use-chat";
import { useMe } from "@/hooks/use-me";
import { useTeamMemberMap } from "@/hooks/use-team-members";

import { conversationTitle } from "./conversationTitle";

/**
 * Selectable conversation rows for the chat rail — Space-style: avatar (channel `#` tile / DM
 * other-member avatar / group), title, last-message preview (author + token-stripped text), and
 * the last-activity time. Recency order + previews come from the backend (one query, no N+1).
 * Selection is a callback (not a route) so a pick swaps the centre panel in place. Optionally
 * filtered (the Channels tab passes `kind === "Channel"`). Channel creation lives in the rail
 * header, not here.
 */
export function ConversationList({
  activeConversationId,
  onSelect,
  filter,
}: {
  activeConversationId: string | null;
  onSelect: (conversationId: string) => void;
  filter?: (conversation: ConversationSummary) => boolean;
}) {
  const conversations = useConversations();
  const members = useTeamMemberMap();
  const me = useMe();
  const myId = me.data?.id ?? null;

  const rows = (conversations.data ?? []).filter(c => (filter ? filter(c) : true));

  return (
    <div className="chat-list">
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
            <ConversationAvatar conversation={c} members={members} myId={myId} />
            <span className="chat-conv-main">
              <span className="chat-conv-top">
                <span className="chat-conv-name">{conversationTitle(c, members, myId)}</span>
                <span className="chat-conv-time">{formatListTime(c.lastActivityDate)}</span>
              </span>
              <PreviewLine conversation={c} members={members} myId={myId} />
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}

function ConversationAvatar({ conversation, members, myId }: { conversation: ConversationSummary; members: Map<string, TeamMemberSummary>; myId: string | null }) {
  if (conversation.kind === "Channel") return <span className="chat-conv-av chat-conv-av-hash">#</span>;

  if (conversation.kind === "Group") {
    return <span className="chat-conv-av chat-conv-av-initial"><Ic.Users size={15} /></span>;
  }

  const otherId = conversation.memberUserIds.find(id => id !== myId) ?? conversation.memberUserIds[0];
  const other = otherId != null ? members.get(otherId) : undefined;

  if (other?.avatarUrl) return <img className="chat-conv-av" src={other.avatarUrl} alt="" />;
  return <span className="chat-conv-av chat-conv-av-initial">{(other?.name ?? "?").charAt(0).toUpperCase()}</span>;
}

function PreviewLine({ conversation, members, myId }: { conversation: ConversationSummary; members: Map<string, TeamMemberSummary>; myId: string | null }) {
  const last = conversation.lastMessage;

  if (last == null) return <span className="chat-conv-preview chat-conv-preview-muted">No messages yet</span>;
  if (last.isDeleted) return <span className="chat-conv-preview chat-conv-preview-deleted">message deleted</span>;

  const author = last.authorUserId === myId ? "You" : members.get(last.authorUserId)?.name ?? "Someone";
  return (
    <span className="chat-conv-preview">
      <span className="chat-conv-preview-author">{author}</span> {last.preview}
    </span>
  );
}

/** Today → time; same year → "Aug 3"; older → "Aug 2020" (Space-style). */
function formatListTime(iso: string): string {
  const date = new Date(iso);
  const now = new Date();

  if (date.toDateString() === now.toDateString()) {
    return date.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  }
  if (date.getFullYear() === now.getFullYear()) {
    return date.toLocaleDateString(undefined, { month: "short", day: "numeric" });
  }
  return date.toLocaleDateString(undefined, { month: "short", year: "numeric" });
}
