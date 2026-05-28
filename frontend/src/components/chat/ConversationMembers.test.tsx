import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ConversationMembers } from "./ConversationMembers";

/**
 * The header members control: the count opens a popover that lists the conversation's members
 * (the caller flagged "(you)") and offers only NON-members under "Add people", inviting one on
 * click. (No self-join — invite is the only way in.)
 */
const add = vi.fn();
vi.mock("@/hooks/use-chat", () => ({ useAddMember: () => ({ mutate: add, isPending: false }) }));
vi.mock("@/hooks/use-me", () => ({ useMe: () => ({ data: { id: "me" } }) }));
vi.mock("@/hooks/use-team-members", () => ({
  useTeamMembers: () => ({
    isLoading: false,
    data: [
      { userId: "me", name: "Me", email: "me@x", avatarUrl: null },
      { userId: "u2", name: "Bob", email: "b@x", avatarUrl: null },
      { userId: "u3", name: "Cara", email: "c@x", avatarUrl: null },
    ],
  }),
}));

describe("ConversationMembers", () => {
  it("shows the count, lists members with (you), and offers only non-members to add", () => {
    add.mockClear();
    render(<ConversationMembers conversationId="c1" memberUserIds={["me", "u2"]} />);

    expect(screen.getByText("2 members")).toBeInTheDocument();
    expect(screen.queryByText("Cara")).not.toBeInTheDocument();   // popover closed initially

    fireEvent.click(screen.getByLabelText("View members"));

    expect(screen.getByText("Me (you)")).toBeInTheDocument();      // caller flagged
    expect(screen.getByText("Bob")).toBeInTheDocument();           // a current member
    expect(screen.getByText("Cara")).toBeInTheDocument();          // a candidate (not yet in)

    fireEvent.click(screen.getByText("Cara"));
    expect(add).toHaveBeenCalledWith("u3");
  });

  it("shows an empty add-state when everyone is already in", () => {
    render(<ConversationMembers conversationId="c1" memberUserIds={["me", "u2", "u3"]} />);

    expect(screen.getByText("3 members")).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText("View members"));
    expect(screen.getByText(/everyone's already here/i)).toBeInTheDocument();
  });
});
