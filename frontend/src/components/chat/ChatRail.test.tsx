import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ChatRail } from "./ChatRail";
import { useChatDock } from "./ChatDockContext";

/**
 * The rail renders only while open, defaults to Home (unfiltered list, no create), and the tabs
 * swap the body: Channels → a channel-filtered list WITH the create affordance, Members → the
 * roster.
 */
vi.mock("./ChatDockContext", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./ChatDockContext")>()),
  useChatDock: vi.fn(),
}));
vi.mock("./ConversationList", () => ({
  ConversationList: ({ filter, showCreate }: { filter?: unknown; showCreate?: boolean }) => (
    <div data-testid="conversation-list" data-filtered={filter ? "true" : "false"} data-create={showCreate ? "true" : "false"} />
  ),
}));
vi.mock("./ChatMemberList", () => ({ ChatMemberList: () => <div data-testid="member-list" /> }));

function mockDock(overrides: Partial<ReturnType<typeof useChatDock>> = {}) {
  vi.mocked(useChatDock).mockReturnValue({
    isOpen: true, activeConversationId: null,
    open: vi.fn(), close: vi.fn(), toggle: vi.fn(), openConversation: vi.fn(), setActiveConversationId: vi.fn(),
    conversationWidth: 420, setConversationWidth: vi.fn(),
    ...overrides,
  });
}

describe("ChatRail", () => {
  beforeEach(() => mockDock());

  it("renders nothing while the dock is closed", () => {
    mockDock({ isOpen: false });
    const { container } = render(<ChatRail />);
    expect(container.firstChild).toBeNull();
  });

  it("defaults to Home: an unfiltered list with no create affordance", () => {
    render(<ChatRail />);
    const list = screen.getByTestId("conversation-list");
    expect(list.getAttribute("data-filtered")).toBe("false");
    expect(list.getAttribute("data-create")).toBe("false");
  });

  it("Channels tab shows a channel-filtered list with create", () => {
    render(<ChatRail />);
    fireEvent.click(screen.getByTitle("Channels"));
    const list = screen.getByTestId("conversation-list");
    expect(list.getAttribute("data-filtered")).toBe("true");
    expect(list.getAttribute("data-create")).toBe("true");
  });

  it("Members tab shows the roster", () => {
    render(<ChatRail />);
    fireEvent.click(screen.getByTitle("Members"));
    expect(screen.getByTestId("member-list")).toBeInTheDocument();
  });
});
