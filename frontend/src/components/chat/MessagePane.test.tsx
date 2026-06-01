import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { MessagePane } from "./MessagePane";

/**
 * The pane interleaves date separators between calendar days and a single "new messages" divider
 * above the first unread message from someone else (anchored on the caller's captured read cursor).
 * The heavy children are stubbed so this isolates the divider wiring; the boundary math itself is
 * unit-tested in lib/messageDividers.test.ts.
 */
vi.mock("./ConversationMembers", () => ({ ConversationMembers: () => <div data-testid="members" /> }));
vi.mock("./MessageComposer", () => ({ MessageComposer: () => <div data-testid="composer" /> }));
vi.mock("./MessageItem", () => ({ MessageItem: ({ message }: { message: { id: string } }) => <div data-testid="msg">{message.id}</div> }));

vi.mock("@/hooks/use-me", () => ({ useMe: () => ({ data: { id: "me" } }) }));
vi.mock("@/hooks/use-team-members", () => ({ useTeamMemberIdentityMap: () => new Map() }));

// Dates relative to "now" so the "Today" / "Yesterday" labels are deterministic on any run date.
const now = new Date();
const yesterday = new Date(now);
yesterday.setDate(now.getDate() - 1);
const iso = (d: Date) => d.toISOString();

const msg = (id: string, createdDate: string) => ({
  id, conversationId: "c1", authorUserId: "other", body: id,
  replyToMessageId: null, createdDate, editedDate: null, isDeleted: false, references: [],
});

// Pages arrive newest-first; the pane flattens + reverses to oldest-first (m1, m2, m3).
const page = { messages: [msg("m3", iso(now)), msg("m2", iso(yesterday)), msg("m1", iso(yesterday))], nextBeforeId: null, hasMore: false };

vi.mock("@/hooks/use-chat", () => ({
  useConversation: () => ({
    data: {
      id: "c1", kind: "Channel", slug: "general", name: "General", description: null,
      visibility: "Public", archived: false, memberCount: 2, memberUserIds: ["me", "other"],
      createdDate: "", lastMessage: null, lastActivityDate: "", lastReadMessageId: "m2",
    },
  }),
  useMessages: () => ({ data: { pages: [page] }, hasNextPage: false, isFetchingNextPage: false, isLoading: false, error: null, fetchNextPage: vi.fn() }),
  usePostMessage: () => ({ mutate: vi.fn(), isPending: false }),
  useMarkRead: () => ({ mutate: vi.fn() }),
}));

describe("MessagePane dividers", () => {
  it("draws day separators and one unread divider above the first unread message", () => {
    render(<MessagePane conversationId="c1" />);

    expect(screen.getByText("Yesterday")).toBeInTheDocument();   // m1 (first message of an earlier day)
    expect(screen.getByText("Today")).toBeInTheDocument();       // m3 crosses into a new day
    expect(screen.getByText("New messages")).toBeInTheDocument();  // cursor m2 → first unread is m3
    expect(screen.getAllByTestId("msg")).toHaveLength(3);
  });
});
