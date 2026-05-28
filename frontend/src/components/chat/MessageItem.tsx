import type { MessageView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";

import { MessageBody } from "./MessageBody";

/**
 * One message row: author avatar/name, timestamp, body (with reference chips). A deleted
 * message renders as a tombstone — the server already blanked its body, so there is nothing
 * to leak. <c>isMine</c> lets the row style the author's own messages distinctly.
 */
export function MessageItem({ message, members, isMine }: { message: MessageView; members: Map<string, TeamMemberSummary>; isMine: boolean }) {
  const name = members.get(message.authorUserId)?.name ?? "Unknown";

  return (
    <div className="chat-msg" data-mine={isMine}>
      <div className="chat-msg-avatar" aria-hidden="true">{name.charAt(0).toUpperCase()}</div>
      <div className="chat-msg-main">
        <div className="chat-msg-head">
          <span className="chat-msg-author">{name}</span>
          <span className="chat-msg-time">{formatTimestamp(message.createdDate)}</span>
          {message.editedDate && !message.isDeleted && <span className="chat-msg-edited">(edited)</span>}
        </div>
        {message.isDeleted ? (
          <span className="chat-msg-deleted">message deleted</span>
        ) : (
          <MessageBody body={message.body} members={members} />
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
