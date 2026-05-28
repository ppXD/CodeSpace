import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import { ChatDockProvider, useChatDock } from "./ChatDockContext";

/**
 * The dock's open/closed + active-conversation state must persist to localStorage so the panel
 * survives navigation and reloads (the whole point of an always-present dock). vitest.setup
 * clears localStorage between tests, so each starts closed/empty.
 */
const wrapper = ({ children }: { children: ReactNode }) => <ChatDockProvider>{children}</ChatDockProvider>;

describe("useChatDock", () => {
  it("starts closed and toggles, persisting open state", () => {
    const { result } = renderHook(() => useChatDock(), { wrapper });

    expect(result.current.isOpen).toBe(false);

    act(() => result.current.toggle());
    expect(result.current.isOpen).toBe(true);
    expect(localStorage.getItem("codespace.chatDock.open")).toBe("1");

    act(() => result.current.close());
    expect(result.current.isOpen).toBe(false);
    expect(localStorage.getItem("codespace.chatDock.open")).toBe("0");
  });

  it("openConversation focuses a conversation and opens the dock", () => {
    const { result } = renderHook(() => useChatDock(), { wrapper });

    act(() => result.current.openConversation("conv-9"));

    expect(result.current.isOpen).toBe(true);
    expect(result.current.activeConversationId).toBe("conv-9");
    expect(localStorage.getItem("codespace.chatDock.conversationId")).toBe("conv-9");
  });

  it("clearing the active conversation removes it from storage", () => {
    const { result } = renderHook(() => useChatDock(), { wrapper });

    act(() => result.current.setActiveConversationId("x"));
    act(() => result.current.setActiveConversationId(null));

    expect(result.current.activeConversationId).toBeNull();
    expect(localStorage.getItem("codespace.chatDock.conversationId")).toBeNull();
  });

  it("conversation width persists and is floored at the minimum", () => {
    const { result } = renderHook(() => useChatDock(), { wrapper });

    expect(result.current.conversationWidth).toBeGreaterThanOrEqual(320);

    act(() => result.current.setConversationWidth(500));
    expect(result.current.conversationWidth).toBe(500);
    expect(localStorage.getItem("codespace.chatDock.conversationWidth")).toBe("500");

    act(() => result.current.setConversationWidth(100));   // below the floor
    expect(result.current.conversationWidth).toBe(320);
  });
});
