import { Ic } from "@/_imported/ai-code-space/icons";
import type { MessageView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";
import { avatarColor } from "@/lib/avatarColor";

import { MessageBody } from "./MessageBody";
import { MessageInteractionCard } from "./MessageInteractionCard";

/**
 * One message row: author avatar/name, timestamp, body (with reference chips). A deleted
 * message renders as a tombstone — the server already blanked its body, so there is nothing
 * to leak. <c>isMine</c> lets the row style the author's own messages distinctly.
 */
export function MessageItem({ message, members, isMine, myUserId }: { message: MessageView; members: Map<string, TeamMemberSummary>; isMine: boolean; myUserId: string | null }) {
  const author = members.get(message.authorUserId);
  const name = author?.name ?? "Unknown";
  const isBot = author?.isBot ?? false;

  // Stable per-author colour so each speaker is recognisable down the log (incl. yourself).
  const color = avatarColor(message.authorUserId);

  return (
    <div className="chat-msg" data-mine={isMine}>
      {/* A bot author gets a distinct robot-icon avatar (no per-author colour) so automated
          messages read as "from a bot" at a glance; humans keep the coloured initial. */}
      <div className="chat-msg-avatar" data-bot={isBot} aria-hidden="true" style={isBot ? undefined : { background: color.bg, color: color.fg }}>
        {isBot ? <Ic.Bot size={16} /> : name.charAt(0).toUpperCase()}
      </div>
      <div className="chat-msg-main">
        <div className="chat-msg-head">
          <span className="chat-msg-author">{name}</span>
          <span className="chat-msg-time">{formatTimestamp(message.createdDate)}</span>
          {message.editedDate && !message.isDeleted && <span className="chat-msg-edited">(edited)</span>}
        </div>
        {message.isDeleted ? (
          <span className="chat-msg-deleted">message deleted</span>
        ) : (
          <>
            <MessageBody body={message.body} members={members} myUserId={myUserId} />
            {message.interaction && (
              <MessageInteractionCard
                interaction={message.interaction}
                members={members}
                conversationId={message.conversationId}
                messageId={message.id}
                myUserId={myUserId}
              />
            )}
          </>
        )}
      </div>
    </div>
  );
}

/** Today → time of day; this year → "Mon D, HH:MM"; older → full date. */
function formatTimestamp(iso: string): string {
  const date = new Date(iso);
  const now = new Date();
  const sameDay = date.toDateString() === now.toDateString();

  if (sameDay) return date.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });

  const time = date.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  return `${date.toLocaleDateString(undefined, { month: "short", day: "numeric" })}, ${time}`;
}
