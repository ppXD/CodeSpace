import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

/**
 * Global state for the persistent chat dock — whether it's open and which conversation it's
 * showing. Lives at the app-shell level so the dock stays mounted across every route (you never
 * navigate away from your code to chat). Persisted to localStorage so the dock survives reloads.
 */
interface ChatDockState {
  isOpen: boolean;
  activeConversationId: string | null;
  open: () => void;
  close: () => void;
  toggle: () => void;
  /** Open the dock AND focus a conversation in one step (e.g. from a future "discuss this PR" action). */
  openConversation: (conversationId: string) => void;
  setActiveConversationId: (conversationId: string | null) => void;
  /** Width (px) of the conversation panel; the resize handle drives it. Persisted + floored. */
  conversationWidth: number;
  setConversationWidth: (width: number) => void;
}

const OPEN_KEY = "codespace.chatDock.open";
const CONVERSATION_KEY = "codespace.chatDock.conversationId";
const WIDTH_KEY = "codespace.chatDock.conversationWidth";

/** The conversation panel never narrows past this (px). */
export const MIN_CONVERSATION_WIDTH = 320;
const DEFAULT_CONVERSATION_WIDTH = 420;

const ChatDockContext = createContext<ChatDockState | null>(null);

function readBool(key: string): boolean {
  return typeof window !== "undefined" && localStorage.getItem(key) === "1";
}

function readString(key: string): string | null {
  return typeof window !== "undefined" ? localStorage.getItem(key) : null;
}

export function ChatDockProvider({ children }: { children: ReactNode }) {
  const [isOpen, setIsOpen] = useState(() => readBool(OPEN_KEY));
  const [activeConversationId, setActiveState] = useState<string | null>(() => readString(CONVERSATION_KEY));

  const setOpen = useCallback((next: boolean) => {
    setIsOpen(next);
    localStorage.setItem(OPEN_KEY, next ? "1" : "0");
  }, []);

  const setActiveConversationId = useCallback((conversationId: string | null) => {
    setActiveState(conversationId);
    if (conversationId) localStorage.setItem(CONVERSATION_KEY, conversationId);
    else localStorage.removeItem(CONVERSATION_KEY);
  }, []);

  const [conversationWidth, setWidthState] = useState<number>(() => {
    const stored = readString(WIDTH_KEY);
    const parsed = stored != null ? Number(stored) : Number.NaN;
    return Number.isFinite(parsed) ? Math.max(MIN_CONVERSATION_WIDTH, parsed) : DEFAULT_CONVERSATION_WIDTH;
  });

  const setConversationWidth = useCallback((width: number) => {
    const clamped = Math.max(MIN_CONVERSATION_WIDTH, Math.round(width));
    setWidthState(clamped);
    localStorage.setItem(WIDTH_KEY, String(clamped));
  }, []);

  const value = useMemo<ChatDockState>(() => ({
    isOpen,
    activeConversationId,
    open: () => setOpen(true),
    close: () => setOpen(false),
    toggle: () => setOpen(!isOpen),
    openConversation: (conversationId: string) => { setActiveConversationId(conversationId); setOpen(true); },
    setActiveConversationId,
    conversationWidth,
    setConversationWidth,
  }), [isOpen, activeConversationId, setOpen, setActiveConversationId, conversationWidth, setConversationWidth]);

  return <ChatDockContext.Provider value={value}>{children}</ChatDockContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components -- the consumer hook is colocated with its provider (standard React context pattern)
export function useChatDock(): ChatDockState {
  const ctx = useContext(ChatDockContext);
  if (ctx == null) throw new Error("useChatDock must be used within a ChatDockProvider");
  return ctx;
}
