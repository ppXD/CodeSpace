import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { MessageView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";

import { MessageItem } from "./MessageItem";

const members = new Map<string, TeamMemberSummary>([
  ["u1", { userId: "u1", name: "Alice", email: "a@x", avatarUrl: null }],
]);

function msg(partial: Partial<MessageView>): MessageView {
  return {
    id: "m", conversationId: "c", authorUserId: "u1", body: "hi", replyToMessageId: null,
    createdDate: new Date().toISOString(), editedDate: null, isDeleted: false, references: [], interaction: null,
    ...partial,
  };
}

describe("MessageItem", () => {
  it("shows the author name resolved from the member map", () => {
    render(<MessageItem message={msg({})} members={members} isMine={false} myUserId={null} />);
    expect(screen.getByText("Alice")).toBeInTheDocument();
  });

  it("keeps your real name on your own messages and flags the row as mine", () => {
    const { container } = render(<MessageItem message={msg({})} members={members} isMine myUserId="u1" />);
    expect(screen.getByText("Alice")).toBeInTheDocument();   // real name, not "You"
    expect(container.querySelector('.chat-msg[data-mine="true"]')).toBeTruthy();
  });

  it("falls back to Unknown for an unmapped author", () => {
    render(<MessageItem message={msg({ authorUserId: "ghost" })} members={members} isMine={false} myUserId={null} />);
    expect(screen.getByText("Unknown")).toBeInTheDocument();
  });

  it("shows an (edited) marker when edited and not deleted", () => {
    render(<MessageItem message={msg({ editedDate: new Date().toISOString() })} members={members} isMine myUserId="u1" />);
    expect(screen.getByText("(edited)")).toBeInTheDocument();
  });

  it("renders a deleted message as a tombstone, hiding the body", () => {
    render(<MessageItem message={msg({ isDeleted: true, body: "" })} members={members} isMine={false} myUserId={null} />);
    expect(screen.getByText("message deleted")).toBeInTheDocument();
  });

  it("renders the interaction card under the body when the message carries one", () => {
    const interaction = {
      version: 1,
      component: { kind: "action_buttons" as const, buttons: [{ key: "approve", label: "Approve", style: "Primary" as const, requiresComment: false }] },
      allowedResponderUserIds: null,
      state: "Open" as const,
      resolution: null,
    };
    render(<MessageItem message={msg({ interaction })} members={members} isMine={false} myUserId={null} />);
    expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument();
  });

  it("does not render the card for a deleted interactive message (tombstone wins)", () => {
    const interaction = {
      version: 1,
      component: { kind: "action_buttons" as const, buttons: [{ key: "approve", label: "Approve", style: "Primary" as const, requiresComment: false }] },
      allowedResponderUserIds: null,
      state: "Open" as const,
      resolution: null,
    };
    render(<MessageItem message={msg({ isDeleted: true, body: "", interaction })} members={members} isMine={false} myUserId={null} />);
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
  });
});
