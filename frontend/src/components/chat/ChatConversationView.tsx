import { Ic } from "@/_imported/ai-code-space/icons";

import { useChatDock } from "./ChatDockContext";
import { MessagePane } from "./MessagePane";

/**
 * The roomy conversation view. When a conversation is selected it opens OVER the centre content
 * (the page stays mounted underneath — closing is instant, never a navigation). Closing returns
 * you to whatever you were looking at; the rail on the right stays put so you can reopen at once.
 */
export function ChatConversationView() {
  const { isOpen, activeConversationId, setActiveConversationId } = useChatDock();

  if (!isOpen || !activeConversationId) return null;

  return (
    <div className="chat-convview">
      <button className="chat-convview-close chrome-btn" title="Close conversation" onClick={() => setActiveConversationId(null)}>
        <Ic.Collapse size={14} />
      </button>
      <MessagePane key={activeConversationId} conversationId={activeConversationId} />
    </div>
  );
}
