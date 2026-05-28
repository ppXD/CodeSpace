import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

import { useChatDock } from "./ChatDockContext";
import { ChatMemberList } from "./ChatMemberList";
import { ConversationList } from "./ConversationList";

/**
 * The persistent right rail — your always-available entry into chat. Tabs (Space-style):
 * Home (all recent conversations), Channels (channels + create), Members (the roster → click
 * opens a DM). Picking anything sets the active conversation, which opens the roomy view over
 * the centre content (see ChatConversationView). Renders nothing while the dock is closed.
 */
type ChatTab = "home" | "channels" | "members";
const TAB_KEY = "codespace.chatRail.tab";

export function ChatRail() {
  const { isOpen, activeConversationId, setActiveConversationId, close } = useChatDock();
  const [tab, setTabState] = useState<ChatTab>(() => (localStorage.getItem(TAB_KEY) as ChatTab) || "home");

  if (!isOpen) return null;

  const setTab = (next: ChatTab) => {
    setTabState(next);
    localStorage.setItem(TAB_KEY, next);
  };

  return (
    <aside className="chat-rail" aria-label="Chats">
      <div className="chat-rail-head">
        <span className="chat-rail-title">Chats</span>
        <button className="chrome-btn" title="Close chat" onClick={close}><Ic.ChevronRight size={14} /></button>
      </div>

      <div className="chat-rail-tabs" role="tablist">
        <button className="chat-rail-tab" role="tab" aria-selected={tab === "home"} data-active={tab === "home"} title="Home" onClick={() => setTab("home")}>
          <Ic.Home size={16} />
        </button>
        <button className="chat-rail-tab" role="tab" aria-selected={tab === "channels"} data-active={tab === "channels"} title="Channels" onClick={() => setTab("channels")}>
          <span className="chat-rail-hash">#</span>
        </button>
        <button className="chat-rail-tab" role="tab" aria-selected={tab === "members"} data-active={tab === "members"} title="Members" onClick={() => setTab("members")}>
          <Ic.Users size={16} />
        </button>
      </div>

      <div className="chat-rail-body">
        {tab === "home" && <ConversationList activeConversationId={activeConversationId} onSelect={setActiveConversationId} />}
        {tab === "channels" && <ConversationList activeConversationId={activeConversationId} onSelect={setActiveConversationId} filter={c => c.kind === "Channel"} showCreate />}
        {tab === "members" && <ChatMemberList onOpened={setActiveConversationId} />}
      </div>
    </aside>
  );
}
