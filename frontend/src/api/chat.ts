import { fetchJson } from "./request";

// ─── Types (mirror backend chat DTOs) ──────────────────────────────────────────

export type ConversationKind = "Direct" | "Group" | "Channel";
export type ConversationVisibility = "Public" | "Private";

export interface ConversationSummary {
  id: string;
  kind: ConversationKind;
  /** Channel handle (rendered as `#slug`); null for DM / group. */
  slug: string | null;
  /** Channel / group display name; null for DM (the UI renders the other member's name). */
  name: string | null;
  description: string | null;
  visibility: ConversationVisibility;
  archived: boolean;
  memberCount: number;
  memberUserIds: string[];
  createdDate: string;
  /** Most-recent message preview for the list row. Null on a single get / a conversation with no messages. */
  lastMessage: MessagePreview | null;
  /** Last message time, else createdDate. The list is sorted on this newest-first. */
  lastActivityDate: string;
}

/** Mirrors backend `MessagePreview` — the conversation's latest message, trimmed for a list row. */
export interface MessagePreview {
  messageId: string;
  authorUserId: string;
  /** Token-stripped, truncated plain text. Empty when the last message is deleted. */
  preview: string;
  createdDate: string;
  isDeleted: boolean;
}

/**
 * One `@`-reference chip on a message. `refType` is an open namespace
 * (`user` / `pull_request` / `workflow` / `code_location` / …) — the renderer keys off it
 * but nothing here is hardcoded to a known set. `label` is the server-cached display text.
 */
export interface MessageReferenceView {
  refType: string;
  refId: string;
  label: string | null;
}

export interface MessageView {
  id: string;
  conversationId: string;
  authorUserId: string;
  /** Markdown + inline `<reftype:refid|label>` tokens. Empty for a deleted tombstone. */
  body: string;
  replyToMessageId: string | null;
  createdDate: string;
  editedDate: string | null;
  isDeleted: boolean;
  references: MessageReferenceView[];
}

/** One newest-first page of history. Scroll up by passing `nextBeforeId` as the next cursor. */
export interface MessagePage {
  messages: MessageView[];
  nextBeforeId: string | null;
  hasMore: boolean;
}

// ─── API client ────────────────────────────────────────────────────────────────

export const chatApi = {
  listConversations: () => fetchJson<ConversationSummary[]>("/api/conversations"),

  getConversation: (conversationId: string) =>
    fetchJson<ConversationSummary>(`/api/conversations/${conversationId}`),

  createChannel: (name: string, slug: string, isPrivate = false) =>
    fetchJson<{ id: string }>("/api/conversations/channels", {
      method: "POST",
      body: JSON.stringify({ name, slug, private: isPrivate }),
    }),

  /** Find-or-create the 1:1 DM with another user (race-safe + idempotent server-side). */
  openDirect: (otherUserId: string) =>
    fetchJson<{ id: string }>("/api/conversations/direct", {
      method: "POST",
      body: JSON.stringify({ otherUserId }),
    }),

  /** Invite a user into a channel/group (idempotent server-side; rejected for DMs). */
  addMember: (conversationId: string, userId: string) =>
    fetchJson<void>(`/api/conversations/${conversationId}/members`, {
      method: "POST",
      body: JSON.stringify({ userId }),
    }),

  listMessages: (conversationId: string, beforeId?: string | null, limit = 50) => {
    const params = new URLSearchParams({ limit: String(limit) });
    if (beforeId) params.set("beforeId", beforeId);
    return fetchJson<MessagePage>(`/api/conversations/${conversationId}/messages?${params}`);
  },

  postMessage: (conversationId: string, body: string, replyToMessageId?: string | null) =>
    fetchJson<MessageView>(`/api/conversations/${conversationId}/messages`, {
      method: "POST",
      body: JSON.stringify({ body, replyToMessageId: replyToMessageId ?? null }),
    }),

  markRead: (conversationId: string, lastReadMessageId: string) =>
    fetchJson<void>(`/api/conversations/${conversationId}/messages/read`, {
      method: "POST",
      body: JSON.stringify({ lastReadMessageId }),
    }),
};
