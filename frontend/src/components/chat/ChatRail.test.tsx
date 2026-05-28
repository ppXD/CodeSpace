import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ChatRail } from "./ChatRail";
import { useChatDock } from "./ChatDockContext";

/**
 * The rail renders only while open, defaults to Home (unfiltered list), and the tabs swap the
 * body (Channels → channel-filtered list, Members → roster). Channel creation is a header "+"
 * that opens an escapable inline form.
 */
vi.mock("./ChatDockContext", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./ChatDockContext")>()),
  useChatDock: vi.fn(),
}));
vi.mock("@/hooks/use-chat", () => ({ useCreateChannel: () => ({ mutateAsync: vi.fn(), isPending: false }) }));
vi.mock("./ConversationList", () => ({
  ConversationList: ({ filter }: { filter?: unknown }) => <div data-testid="conversation-list" data-filtered={filter ? "true" : "false"} />,
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

  it("defaults to Home: an unfiltered list", () => {
    render(<ChatRail />);
    expect(screen.getByTestId("conversation-list").getAttribute("data-filtered")).toBe("false");
  });

  it("Channels tab shows a channel-filtered list", () => {
    render(<ChatRail />);
    fireEvent.click(screen.getByTitle("Channels"));
    expect(screen.getByTestId("conversation-list").getAttribute("data-filtered")).toBe("true");
  });

  it("Members tab shows the roster", () => {
    render(<ChatRail />);
    fireEvent.click(screen.getByTitle("Members"));
    expect(screen.getByTestId("member-list")).toBeInTheDocument();
  });

  it("the header + reveals a create form, and Cancel dismisses it", () => {
    render(<ChatRail />);
    expect(screen.queryByPlaceholderText("New channel name")).not.toBeInTheDocument();

    fireEvent.click(screen.getByLabelText("New channel"));
    expect(screen.getByPlaceholderText("New channel name")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));
    expect(screen.queryByPlaceholderText("New channel name")).not.toBeInTheDocument();
  });
});
