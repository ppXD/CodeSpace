import { Ic } from "@/_imported/ai-code-space/icons";

import { ChatDockProvider, useChatDock } from "./ChatDockContext";
import { ConversationList } from "./ConversationList";
import { MessagePane } from "./MessagePane";

export { ChatDockProvider };

/**
 * The persistent right-side chat panel. Always mounted in the app shell (see _app.tsx) so chat
 * stays put across every route — you read code / a PR on the left and chat on the right without
 * navigating away. A thin bar carries back (to the conversation list) + close; the body is the
 * conversation list, or the selected conversation's message pane. Renders nothing while closed,
 * so no chat queries fire until you open it.
 */
export function ChatDock() {
  const { isOpen, activeConversationId, close, setActiveConversationId } = useChatDock();

  if (!isOpen) return null;

  return (
    <aside className="chat-dock" aria-label="Chat">
      <div className="chat-dock-bar">
        {activeConversationId ? (
          <button className="chrome-btn" title="Back to conversations" onClick={() => setActiveConversationId(null)}>
            <Ic.ArrowLeft size={14} />
          </button>
        ) : (
          <span className="chat-dock-title">Chat</span>
        )}
        <span className="chat-dock-grow" />
        <button className="chrome-btn" title="Close chat" onClick={close}>
          <Ic.ChevronRight size={14} />
        </button>
      </div>

      {activeConversationId ? (
        <MessagePane conversationId={activeConversationId} />
      ) : (
        <ConversationList activeConversationId={activeConversationId} onSelect={setActiveConversationId} />
      )}
    </aside>
  );
}
