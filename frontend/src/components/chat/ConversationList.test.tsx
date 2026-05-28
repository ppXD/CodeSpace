import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ConversationList } from "./ConversationList";

/**
 * The list is now router-free (selection is a callback, not a Link) so it works inside the
 * dock without navigating. These pin: rows render per kind, clicking fires onSelect with the
 * id, and the active row is marked.
 */
vi.mock("@/hooks/use-chat", () => ({
  useConversations: () => ({
    isLoading: false,
    data: [
      { id: "c1", kind: "Channel", slug: "general", name: "General", description: null, visibility: "Public", archived: false, memberCount: 2, memberUserIds: [], createdDate: "" },
      { id: "c2", kind: "Direct", slug: null, name: null, description: null, visibility: "Public", archived: false, memberCount: 2, memberUserIds: ["me", "u2"], createdDate: "" },
    ],
  }),
  useCreateChannel: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));
vi.mock("@/hooks/use-me", () => ({ useMe: () => ({ data: { id: "me" } }) }));
vi.mock("@/hooks/use-team-members", () => ({
  useTeamMemberMap: () => new Map([["u2", { userId: "u2", name: "Bob", email: "b@x", avatarUrl: null }]]),
}));

describe("ConversationList", () => {
  it("renders a channel as #slug and a DM as the other member, and fires onSelect", () => {
    const onSelect = vi.fn();
    render(<ConversationList activeConversationId={null} onSelect={onSelect} />);

    const channel = screen.getByText("#general");
    expect(channel).toBeInTheDocument();
    expect(screen.getByText("Bob")).toBeInTheDocument();

    fireEvent.click(channel);
    expect(onSelect).toHaveBeenCalledWith("c1");
  });

  it("marks the active conversation row", () => {
    render(<ConversationList activeConversationId="c1" onSelect={() => {}} />);
    expect(screen.getByText("#general").closest("button")?.getAttribute("data-active")).toBe("true");
  });
});
