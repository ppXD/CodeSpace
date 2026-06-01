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
  /** The caller's read cursor (last message id they've seen). Populated only on a single get;
   * null in the list and until the caller has read anything. Drives the pane's unread divider. */
  lastReadMessageId: string | null;
}

/** Mirrors backend `MessagePreview` — the conversation's latest message, trimmed for a list row. */
export interface MessagePreview {
  messageId: string;
  authorUserId: string;
  /** Token-stripped, truncated plain text. Empty when the last message is deleted. */
  preview: string;
  createdDate: string;
  isDeleted: boolean;
  /** True when this last message @-mentions the current user — the list flags "you were mentioned". */
  mentionsViewer: boolean;
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

// ─── Interactive components (mirror backend MessageInteractionView — token-stripped) ────────────

export type InteractionButtonStyle = "Default" | "Primary" | "Danger";
export type InteractionState = "Open" | "Resolved" | "Expired";

export interface InteractionButton {
  key: string;
  label: string;
  style: InteractionButtonStyle;
  /** When true the UI must collect a comment before submitting this button (e.g. "request changes"). */
  requiresComment: boolean;
}

/** Polymorphic by `kind` (mirrors backend `InteractionComponent`). */
export interface ActionButtonsComponent {
  kind: "action_buttons";
  buttons: InteractionButton[];
}

/** A form the responder fills in chat; the submitted values are injected into the run. */
export interface FormComponent {
  kind: "form";
  /** JSON Schema describing the fields (rendered by the schema-driven form). */
  fields: Record<string, unknown>;
  submitLabel: string;
}

export type InteractionComponent = ActionButtonsComponent | FormComponent;

/** The outcome once a response lands — the display mirror of the resolved workflow wait. */
export interface InteractionResolution {
  responseKey: string;
  byUserId: string;
  comment: string | null;
  /** For a form response — the submitted field values. Null for a button response. */
  values: Record<string, unknown> | null;
  atUtc: string;
}

/**
 * The client-facing projection of a message's interaction. Deliberately omits the routing target —
 * the wait token never reaches the client; a response is keyed by message id and the backend
 * re-derives the target. Null for a plain message.
 */
export interface MessageInteractionView {
  version: number;
  component: InteractionComponent;
  /** Null = any active member may respond; otherwise only these users (e.g. the picked reviewer). */
  allowedResponderUserIds: string[] | null;
  state: InteractionState;
  resolution: InteractionResolution | null;
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
  /** An attached interactive component (e.g. a review card), else null for a plain message. */
  interaction: MessageInteractionView | null;
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

  /** Respond to an interactive message (click a button, or submit a form's `values`). The wait token
   *  stays server-side — the message id identifies the interaction; the backend re-derives the target. */
  respondToMessage: (conversationId: string, messageId: string, responseKey: string, comment: string | null, values: Record<string, unknown> | null = null) =>
    fetchJson<void>(`/api/conversations/${conversationId}/messages/${messageId}/respond`, {
      method: "POST",
      body: JSON.stringify({ responseKey, comment, values }),
    }),
};
