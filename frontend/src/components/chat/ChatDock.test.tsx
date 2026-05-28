import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ChatDock } from "./ChatDock";
import { useChatDock } from "./ChatDockContext";

/**
 * The dock is thin composition: nothing while closed (so no chat queries fire), the
 * conversation list when open with no selection, the message pane when a conversation is
 * active. Children + the context hook are stubbed so this pins only that branching.
 */
vi.mock("./ChatDockContext", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./ChatDockContext")>()),
  useChatDock: vi.fn(),
}));
vi.mock("./ConversationList", () => ({ ConversationList: () => <div data-testid="conversation-list" /> }));
vi.mock("./MessagePane", () => ({ MessagePane: ({ conversationId }: { conversationId: string }) => <div data-testid="message-pane">{conversationId}</div> }));

function mockDock(overrides: Partial<ReturnType<typeof useChatDock>>) {
  vi.mocked(useChatDock).mockReturnValue({
    isOpen: true, activeConversationId: null,
    open: vi.fn(), close: vi.fn(), toggle: vi.fn(), openConversation: vi.fn(), setActiveConversationId: vi.fn(),
    ...overrides,
  });
}

describe("ChatDock", () => {
  it("renders nothing while closed", () => {
    mockDock({ isOpen: false });
    const { container } = render(<ChatDock />);
    expect(container.firstChild).toBeNull();
  });

  it("shows the conversation list when open with no active conversation", () => {
    mockDock({ isOpen: true, activeConversationId: null });
    render(<ChatDock />);
    expect(screen.getByTestId("conversation-list")).toBeInTheDocument();
    expect(screen.queryByTestId("message-pane")).not.toBeInTheDocument();
  });

  it("shows the message pane for the active conversation", () => {
    mockDock({ isOpen: true, activeConversationId: "c9" });
    render(<ChatDock />);
    expect(screen.getByTestId("message-pane")).toHaveTextContent("c9");
    expect(screen.queryByTestId("conversation-list")).not.toBeInTheDocument();
  });
});
