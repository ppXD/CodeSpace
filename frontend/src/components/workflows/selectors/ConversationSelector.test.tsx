import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ConversationSelector } from "./ConversationSelector";

/**
 * The conversation picker (`x-selector: "conversation"`) renders the shared SearchSelect combobox and saves
 * the chosen id. Options appear once the search box is focused. Hook mocked: useConversations.
 */
vi.mock("@/hooks/use-chat", () => ({
  useConversations: () => ({
    isLoading: false,
    data: [
      { id: "c1", kind: "Channel", slug: "general", name: "General" },
      { id: "c2", kind: "Group", slug: null, name: "Release Squad" },
      { id: "c3", kind: "Direct", slug: null, name: null },
    ],
  }),
}));

describe("ConversationSelector", () => {
  it("lists conversations with friendly labels and emits the chosen id", () => {
    const onChange = vi.fn();
    render(<ConversationSelector value="" onChange={onChange} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a conversation…" }));
    expect(screen.getByRole("option", { name: "#general" })).toBeInTheDocument();          // channel → #slug
    expect(screen.getByRole("option", { name: "Release Squad" })).toBeInTheDocument();      // named group → name
    expect(screen.getByRole("option", { name: "(direct message)" })).toBeInTheDocument();   // nameless DM → generic

    fireEvent.mouseDown(screen.getByRole("option", { name: "#general" }));
    expect(onChange).toHaveBeenCalledWith("c1");
  });

  it("reflects the currently-selected conversation as a chip", () => {
    render(<ConversationSelector value="c2" onChange={() => {}} />);
    expect(screen.getByText("Release Squad")).toBeInTheDocument();
  });
});
