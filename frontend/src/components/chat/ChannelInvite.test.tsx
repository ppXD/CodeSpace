import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ChannelInvite } from "./ChannelInvite";

/**
 * The invite dropdown offers only team members not already in the conversation, and clicking one
 * invites them via AddMember. (No self-join — invite is the only way in.)
 */
const add = vi.fn();
vi.mock("@/hooks/use-chat", () => ({ useAddMember: () => ({ mutate: add, isPending: false }) }));
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

describe("ChannelInvite", () => {
  it("offers only members not already in the channel, and adds on click", () => {
    add.mockClear();
    render(<ChannelInvite conversationId="c1" memberUserIds={["me", "u2"]} />);

    expect(screen.queryByText("Cara")).not.toBeInTheDocument();   // closed initially

    fireEvent.click(screen.getByLabelText("Add people"));

    expect(screen.getByText("Cara")).toBeInTheDocument();
    expect(screen.queryByText("Bob")).not.toBeInTheDocument();    // already a member
    expect(screen.queryByText("Me")).not.toBeInTheDocument();     // already a member

    fireEvent.click(screen.getByText("Cara"));
    expect(add).toHaveBeenCalledWith("u3");
  });

  it("shows an empty state when everyone is already in", () => {
    render(<ChannelInvite conversationId="c1" memberUserIds={["me", "u2", "u3"]} />);
    fireEvent.click(screen.getByLabelText("Add people"));
    expect(screen.getByText(/everyone's already here/i)).toBeInTheDocument();
  });
});
