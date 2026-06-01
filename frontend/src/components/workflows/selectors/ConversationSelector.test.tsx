import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ConversationSelector } from "./ConversationSelector";

/**
 * The conversation picker is dispatched via `x-selector: "conversation"` on a node input (e.g. a
 * chat-posting node's target). It lists the team's conversations and saves the chosen id as a plain
 * string. Hook mocked: useConversations.
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

    expect(screen.getByRole("option", { name: "#general" })).toBeInTheDocument();          // channel → #slug
    expect(screen.getByRole("option", { name: "Release Squad" })).toBeInTheDocument();      // named group → name
    expect(screen.getByRole("option", { name: "(direct message)" })).toBeInTheDocument();   // nameless DM → generic

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "c1" } });
    expect(onChange).toHaveBeenCalledWith("c1");
  });

  it("reflects the currently-selected conversation id", () => {
    render(<ConversationSelector value="c2" onChange={() => {}} />);
    expect((screen.getByRole("combobox") as HTMLSelectElement).value).toBe("c2");
  });
});
