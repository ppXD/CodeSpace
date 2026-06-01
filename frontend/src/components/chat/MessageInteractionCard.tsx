import { Ic } from "@/_imported/ai-code-space/icons";
import type { InteractionButtonStyle, MessageInteractionView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";

/** Button visual emphasis → the app's shared button classes (warm Claude theme). */
const STYLE_CLASS: Record<InteractionButtonStyle, string> = {
  Default: "btn",
  Primary: "btn btn-primary",
  Danger: "btn btn-danger",
};

/**
 * The interactive component attached to a message — today a row of action buttons (the review card).
 * Read-only here: an Open card shows its buttons (not yet wired — clicking is added with the respond
 * endpoint); a Resolved / Expired card shows the outcome stamp instead. The view carries no wait
 * token (the server strips it), so this never has the means to act on its own.
 */
export function MessageInteractionCard({ interaction, members }: { interaction: MessageInteractionView; members: Map<string, TeamMemberSummary> }) {
  const { buttons } = interaction.component;
  const open = interaction.state === "Open";

  return (
    <div className="chat-card" data-state={interaction.state}>
      {open ? (
        <div className="chat-card-actions">
          {buttons.map(b => (
            <button key={b.key} type="button" className={STYLE_CLASS[b.style]} disabled>{b.label}</button>
          ))}
        </div>
      ) : (
        <ResolutionStamp interaction={interaction} members={members} />
      )}
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
