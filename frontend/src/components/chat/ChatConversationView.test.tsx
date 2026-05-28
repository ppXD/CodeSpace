import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ChatConversationView } from "./ChatConversationView";
import { useChatDock } from "./ChatDockContext";

/**
 * The centre view shows only when the dock is open AND a conversation is active; closing it
 * clears the active conversation (returns to the page) without touching isOpen (rail stays).
 */
vi.mock("./ChatDockContext", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./ChatDockContext")>()),
  useChatDock: vi.fn(),
}));
vi.mock("./MessagePane", () => ({ MessagePane: ({ conversationId }: { conversationId: string }) => <div data-testid="message-pane">{conversationId}</div> }));

const setActive = vi.fn();
function mockDock(overrides: Partial<ReturnType<typeof useChatDock>>) {
  vi.mocked(useChatDock).mockReturnValue({
    isOpen: true, activeConversationId: "c1",
    open: vi.fn(), close: vi.fn(), toggle: vi.fn(), openConversation: vi.fn(), setActiveConversationId: setActive,
    ...overrides,
  });
}

describe("ChatConversationView", () => {
  it("renders nothing while the dock is closed", () => {
    mockDock({ isOpen: false });
    const { container } = render(<ChatConversationView />);
    expect(container.firstChild).toBeNull();
  });

  it("renders nothing when no conversation is active", () => {
    mockDock({ isOpen: true, activeConversationId: null });
    const { container } = render(<ChatConversationView />);
    expect(container.firstChild).toBeNull();
  });

  it("shows the active conversation and closes back to the page (clears active, keeps dock open)", () => {
    setActive.mockClear();
    mockDock({ isOpen: true, activeConversationId: "c9" });
    render(<ChatConversationView />);

    expect(screen.getByTestId("message-pane")).toHaveTextContent("c9");

    fireEvent.click(screen.getByTitle("Close conversation"));
    expect(setActive).toHaveBeenCalledWith(null);
  });
});
