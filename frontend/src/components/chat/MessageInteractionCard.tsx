import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { InteractionButton, InteractionButtonStyle, MessageInteractionView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";
import { useRespondToMessage } from "@/hooks/use-chat";

/** Button visual emphasis → the app's shared button classes (warm Claude theme). */
const STYLE_CLASS: Record<InteractionButtonStyle, string> = {
  Default: "btn",
  Primary: "btn btn-primary",
  Danger: "btn btn-danger",
};

/**
 * The interactive component attached to a message — today a row of action buttons (the review card).
 * An Open card's buttons resolve the parked workflow wait via the respond endpoint (keyed by message
 * id — the wait token never reaches the client); a button that requires a comment first reveals an
 * inline composer. A Resolved / Expired card shows the outcome stamp instead. Once a response lands
 * the message refetches and the card re-renders settled.
 */
export function MessageInteractionCard({ interaction, members, conversationId, messageId, myUserId }: {
  interaction: MessageInteractionView;
  members: Map<string, TeamMemberSummary>;
  conversationId: string;
  messageId: string;
  myUserId: string | null;
}) {
  const respond = useRespondToMessage(conversationId);
  const [commenting, setCommenting] = useState<InteractionButton | null>(null);
  const [comment, setComment] = useState("");

  if (interaction.state !== "Open") {
    return (
      <div className="chat-card" data-state={interaction.state}>
        <ResolutionStamp interaction={interaction} members={members} />
      </div>
    );
  }

  const allowed = interaction.allowedResponderUserIds;
  const canRespond = allowed == null || (myUserId != null && allowed.includes(myUserId));
  const pending = respond.isPending;

  const submit = (responseKey: string, value: string | null) => respond.mutate({ messageId, responseKey, comment: value });

  const onClick = (button: InteractionButton) => {
    if (button.requiresComment) {
      setComment("");
      setCommenting(button);
      return;
    }

    submit(button.key, null);
  };

  return (
    <div className="chat-card" data-state="Open">
      {commenting ? (
        <div className="chat-card-comment">
          <textarea
            className="chat-card-comment-input"
            value={comment}
            onChange={e => setComment(e.target.value)}
            placeholder={`Add a comment for “${commenting.label}”`}
            autoFocus
          />
          <div className="chat-card-comment-actions">
            <button type="button" className="btn btn-ghost" onClick={() => setCommenting(null)} disabled={pending}>Cancel</button>
            <button type="button" className={STYLE_CLASS[commenting.style]} onClick={() => submit(commenting.key, comment.trim())} disabled={pending || comment.trim() === ""}>{commenting.label}</button>
          </div>
        </div>
      ) : (
        <div className="chat-card-actions">
          {interaction.component.buttons.map(b => (
            <button key={b.key} type="button" className={STYLE_CLASS[b.style]} onClick={() => onClick(b)} disabled={pending || !canRespond}>{b.label}</button>
          ))}
        </div>
      )}

      {!canRespond && <span className="chat-card-hint">Only the requested reviewer can respond.</span>}
    </div>
  );
}

/** The outcome line on a settled card: the chosen action's label, who responded, and any comment. */
function ResolutionStamp({ interaction, members }: { interaction: MessageInteractionView; members: Map<string, TeamMemberSummary> }) {
  const resolution = interaction.resolution;

  if (resolution == null) return <span className="chat-card-stamp chat-card-stamp-muted">Expired</span>;

  const label = interaction.component.buttons.find(b => b.key === resolution.responseKey)?.label ?? resolution.responseKey;
  const by = members.get(resolution.byUserId)?.name ?? "Unknown";

  return (
    <div className="chat-card-stamp">
      <Ic.Check size={13} />
      <span className="chat-card-stamp-label">{label}</span>
      <span className="chat-card-stamp-by">by {by}</span>
      {resolution.comment && <span className="chat-card-stamp-comment">“{resolution.comment}”</span>}
    </div>
  );
}
