import { useEffect, type PointerEvent as ReactPointerEvent } from "react";

import { MIN_CONVERSATION_WIDTH, useChatDock } from "./ChatDockContext";
import { MessagePane } from "./MessagePane";

/**
 * The conversation panel — its own resizable column between the page content and the Chats rail,
 * so it sits BESIDE your code rather than covering it. The left-edge handle is dual-purpose:
 * click it to close the conversation (back to the page, rail stays), or drag it left/right to
 * resize. Width is floored at MIN_CONVERSATION_WIDTH, capped to keep the content column usable,
 * and re-clamped on viewport resize (responsive). Persisted via the dock context.
 */
const DRAG_THRESHOLD = 4;

function maxWidth(): number {
  // Leave room for the sidebar + a usable content column + the rail.
  return Math.max(MIN_CONVERSATION_WIDTH, window.innerWidth - 640);
}

export function ChatConversationView() {
  const { isOpen, activeConversationId, setActiveConversationId, conversationWidth, setConversationWidth } = useChatDock();

  // Responsive: if the window shrinks, pull the panel back within the usable range.
  useEffect(() => {
    if (!isOpen || !activeConversationId) return;

    const clamp = () => setConversationWidth(Math.min(conversationWidth, maxWidth()));
    window.addEventListener("resize", clamp);
    return () => window.removeEventListener("resize", clamp);
  }, [isOpen, activeConversationId, conversationWidth, setConversationWidth]);

  if (!isOpen || !activeConversationId) return null;

  const onHandleDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = conversationWidth;
    let dragged = false;

    document.body.classList.add("chat-resizing");

    const onMove = (ev: PointerEvent) => {
      const delta = startX - ev.clientX;   // drag left → positive → wider
      if (Math.abs(delta) > DRAG_THRESHOLD) dragged = true;
      setConversationWidth(Math.min(maxWidth(), startWidth + delta));
    };

    const onUp = () => {
      document.body.classList.remove("chat-resizing");
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      if (!dragged) setActiveConversationId(null);   // a click (no drag) closes the conversation
    };

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
  };

  return (
    <aside className="chat-conv-panel">
      <div
        className="chat-conv-resize"
        role="separator"
        aria-orientation="vertical"
        title="Drag to resize · click to close"
        onPointerDown={onHandleDown}
      >
        <span className="chat-conv-resize-grip" />
      </div>
      <MessagePane key={activeConversationId} conversationId={activeConversationId} />
    </aside>
  );
}
