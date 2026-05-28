import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ChatConversationView } from "./ChatConversationView";
import { useChatDock } from "./ChatDockContext";

/**
 * The panel shows only when the dock is open AND a conversation is active. Its left handle is
 * dual-purpose: a plain click (no drag) closes the conversation — clearing the active id but
 * leaving the dock open (rail stays).
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
    conversationWidth: 420, setConversationWidth: vi.fn(),
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

  it("shows the active conversation's pane", () => {
    mockDock({ isOpen: true, activeConversationId: "c9" });
    render(<ChatConversationView />);
    expect(screen.getByTestId("message-pane")).toHaveTextContent("c9");
  });

  it("clicking the handle without dragging closes the conversation", () => {
    setActive.mockClear();
    mockDock({ isOpen: true, activeConversationId: "c9" });
    render(<ChatConversationView />);

    const handle = screen.getByTitle("Drag to resize · click to close");
    fireEvent.pointerDown(handle, { clientX: 500 });
    fireEvent.pointerUp(window, { clientX: 500 });   // released at the same x → a click, not a drag

    expect(setActive).toHaveBeenCalledWith(null);
  });
});
