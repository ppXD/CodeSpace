import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ChatMemberList } from "./ChatMemberList";

/**
 * Members excludes the current user; clicking one find-or-creates the DM and reports its id so
 * the rail can open it in the centre.
 */
const mutateAsync = vi.fn().mockResolvedValue({ id: "dm-1" });

vi.mock("@/hooks/use-team-members", () => ({
  useTeamMembers: () => ({
    isLoading: false,
    data: [
      { userId: "me", name: "Me", email: "me@x", avatarUrl: null },
      { userId: "u2", name: "Bob", email: "b@x", avatarUrl: null },
    ],
  }),
}));
vi.mock("@/hooks/use-me", () => ({ useMe: () => ({ data: { id: "me" } }) }));
vi.mock("@/hooks/use-chat", () => ({ useOpenDirect: () => ({ mutateAsync, isPending: false }) }));

describe("ChatMemberList", () => {
  it("lists other members (not me) and opens a DM on click", async () => {
    const onOpened = vi.fn();
    render(<ChatMemberList onOpened={onOpened} />);

    expect(screen.queryByText("Me")).not.toBeInTheDocument();
    expect(screen.getByText("Bob")).toBeInTheDocument();

    fireEvent.click(screen.getByText("Bob"));

    await waitFor(() => expect(onOpened).toHaveBeenCalledWith("dm-1"));
    expect(mutateAsync).toHaveBeenCalledWith("u2");
  });
});
