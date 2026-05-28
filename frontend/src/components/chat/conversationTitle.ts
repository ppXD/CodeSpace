import type { ConversationSummary } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";

/**
 * The display title for a conversation, by kind:
 *  - Channel → <c>#slug</c> (falls back to name, then "channel");
 *  - Group   → its name (or "Group");
 *  - Direct  → the OTHER participant's name (a DM has no name of its own).
 * Pure — the list and the pane header both render through this so they never disagree.
 */
export function conversationTitle(
  conversation: ConversationSummary,
  members: Map<string, TeamMemberSummary>,
  myUserId: string | null,
): string {
  if (conversation.kind === "Channel") return `#${conversation.slug ?? conversation.name ?? "channel"}`;
  if (conversation.kind === "Group") return conversation.name ?? "Group";

  const otherId = conversation.memberUserIds.find(id => id !== myUserId) ?? conversation.memberUserIds[0];
  return (otherId != null ? members.get(otherId)?.name : undefined) ?? "Direct message";
}
